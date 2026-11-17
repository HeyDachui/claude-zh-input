// Private stdin/stdout inference host. No sockets, prompt files or text logging.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <fcntl.h>
#include <io.h>
#include <algorithm>
#include <bit>
#include <chrono>
#include <filesystem>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>
#include "llama.h"
#include "json.hpp"

using json = nlohmann::json;

// Estimate the thread count from available physical performance cores, not
// logical/SMT threads. Windows still controls placement; this does not pin
// threads to performance cores. Count only the current processor group.
int TranslationThreads() {
    SYSTEM_INFO system{};
    GetSystemInfo(&system);
    int fallback = std::clamp((int)system.dwNumberOfProcessors, 1, 4);
    GROUP_AFFINITY group{};
    DWORD_PTR processMask = 0, systemMask = 0;
    if (!GetThreadGroupAffinity(GetCurrentThread(), &group) ||
        !GetProcessAffinityMask(GetCurrentProcess(), &processMask, &systemMask) || !processMask)
        return fallback;
    fallback = std::min(fallback, (int)std::popcount(processMask));
    DWORD bytes = 0;
    GetLogicalProcessorInformationEx(RelationProcessorCore, nullptr, &bytes);
    if (!bytes || bytes > 1024 * 1024) return fallback;
    std::vector<unsigned char> buffer(bytes);
    if (!GetLogicalProcessorInformationEx(RelationProcessorCore,
        reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buffer.data()), &bytes))
        return fallback;
    int counts[256]{};
    for (DWORD offset = 0; offset < bytes;) {
        constexpr size_t headerSize = offsetof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX, Processor);
        if (bytes - offset < headerSize) return fallback;
        auto info = reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buffer.data() + offset);
        if (info->Size < headerSize || info->Size > bytes - offset)
            return fallback;
        if (info->Relationship == RelationProcessorCore) {
            // A processor-core record has exactly one group mask.
            if (info->Size < headerSize + sizeof(PROCESSOR_RELATIONSHIP) || info->Processor.GroupCount != 1)
                return fallback;
            const auto& mask = info->Processor.GroupMask[0];
            if (mask.Group == group.Group && (mask.Mask & processMask))
                ++counts[info->Processor.EfficiencyClass];
        }
        offset += info->Size;
    }
    for (int efficiency = 255; efficiency >= 0; --efficiency)
        if (counts[efficiency]) return std::min(8, counts[efficiency]);
    return fallback;
}
std::string Utf8(const std::wstring& value) {
    const int n = WideCharToMultiByte(CP_UTF8, 0, value.data(), (int)value.size(), nullptr, 0, nullptr, nullptr);
    std::string result(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), (int)value.size(), result.data(), n, nullptr, nullptr);
    return result;
}
template<class T> T Symbol(HMODULE library, const char* name) {
    auto symbol = GetProcAddress(library, name);
    if (!symbol) throw std::runtime_error("runtime_abi_mismatch");
    return reinterpret_cast<T>(symbol);
}
#define API(name) decltype(&name) name = Symbol<decltype(&::name)>(library, #name)
struct Api {
    HMODULE library;
    API(llama_log_set); API(llama_backend_init); API(llama_backend_free);
    API(llama_model_default_params); API(llama_context_default_params);
    API(llama_model_load_from_file); API(llama_model_free); API(llama_model_get_vocab);
    API(llama_init_from_model); API(llama_free); API(llama_get_memory); API(llama_memory_clear);
    API(llama_tokenize); API(llama_token_to_piece); API(llama_vocab_is_eog);
    API(llama_batch_ext_init); API(llama_batch_ext_free); API(llama_batch_ext_clear);
    API(llama_batch_ext_add_token); API(llama_batch_ext_set_pos); API(llama_batch_ext_set_output_logits);
    API(llama_process); API(llama_sampler_init_greedy); API(llama_sampler_sample);
    API(llama_sampler_reset); API(llama_sampler_free);
    API(llama_state_seq_get_size); API(llama_state_seq_get_data); API(llama_state_seq_set_data);
};
#undef API

class Engine {
    Api api;
    llama_model* model = nullptr;
    llama_context* context = nullptr;
    llama_sampler* sampler = nullptr;
    llama_batch_ext* batch = nullptr;
    const llama_vocab* vocab = nullptr;
    std::vector<llama_token> prefixTokens;
    std::vector<uint8_t> prefixState;
    HANDLE cancelEvent = nullptr;
    int promptThreads = 4;
public:
    Engine(HMODULE library, const std::string& path) : api{library} {
        wchar_t eventName[160]{};
        if (GetEnvironmentVariableW(L"CZI_TRANSLATION_CANCEL_EVENT", eventName, ARRAYSIZE(eventName))) {
            cancelEvent = OpenEventW(SYNCHRONIZE, FALSE, eventName);
            if (!cancelEvent) throw std::runtime_error("cancel_channel_unavailable");
        }
        api.llama_log_set([](ggml_log_level, const char*, void*) {}, nullptr);
        api.llama_backend_init();
        auto mp = api.llama_model_default_params();
        mp.n_gpu_layers = 0;
        model = api.llama_model_load_from_file(path.c_str(), mp);
        if (!model) throw std::runtime_error("model_load_failed");
        vocab = api.llama_model_get_vocab(model);
        auto cp = api.llama_context_default_params();
        cp.n_ctx = 4096; cp.n_batch = 512; cp.n_ubatch = 256; cp.n_seq_max = 1;
        promptThreads = TranslationThreads();
        // Keep the established generation math/threading. Raising generation
        // threads changed a read-only instruction in the semantic regression.
        cp.n_threads = 4; cp.n_threads_batch = promptThreads; cp.no_perf = true;
        cp.offload_kqv = false; cp.op_offload = false;
        cp.abort_callback = [](void* self) { return static_cast<Engine*>(self)->Cancelled(); };
        cp.abort_callback_data = this;
        context = api.llama_init_from_model(model, cp);
        if (!context) throw std::runtime_error("context_load_failed");
        sampler = api.llama_sampler_init_greedy();
        batch = api.llama_batch_ext_init(context);
        if (!sampler || !batch) throw std::runtime_error("runtime_initialization_failed");
    }
    ~Engine() {
        if (batch) api.llama_batch_ext_free(batch);
        if (sampler) api.llama_sampler_free(sampler);
        if (context) api.llama_free(context);
        if (model) api.llama_model_free(model);
        api.llama_backend_free();
        if (cancelEvent) CloseHandle(cancelEvent);
    }
    bool Cancelled() const { return cancelEvent && WaitForSingleObject(cancelEvent, 0) == WAIT_OBJECT_0; }
    void CheckCancelled() const { if (Cancelled()) throw std::runtime_error("cancelled"); }
    void ClearRequest() { api.llama_memory_clear(api.llama_get_memory(context), true); api.llama_sampler_reset(sampler); }
    void Decode(const llama_token* values, int count, int start) {
        CheckCancelled();
        api.llama_batch_ext_clear(batch);
        for (int i = 0; i < count; ++i) {
            int index = api.llama_batch_ext_add_token(batch, 0, values[i]);
            const llama_pos pos = start + i;
            api.llama_batch_ext_set_pos(batch, index, &pos);
        }
        api.llama_batch_ext_set_output_logits(batch, count - 1, true);
        const auto status = api.llama_process(context, LLAMA_PROCESS_TYPE_DECODE, batch);
        CheckCancelled();
        if (status)
            throw std::runtime_error("inference_failed");
    }
    json Run(const json& request) {
        // Also clear partial recurrent state on cancellation and decode errors.
        struct Cleanup { Engine* engine; ~Cleanup() { engine->ClearRequest(); } } cleanup{this};
        CheckCancelled();
        auto prompt = request.at("prompt").get<std::string>();
        const int limit = std::clamp(request.value("max_tokens", 768), 1, 1024);
        if (prompt.empty() || prompt.size() > 32768) throw std::runtime_error("input_length");
        api.llama_memory_clear(api.llama_get_memory(context), true);
        api.llama_sampler_reset(sampler);
        int count = -api.llama_tokenize(vocab, prompt.data(), (int)prompt.size(), nullptr, 0, true, true);
        if (count <= 0 || count + limit > 4096) throw std::runtime_error("context_length");
        std::vector<llama_token> tokens(count);
        if (api.llama_tokenize(vocab, prompt.data(), (int)prompt.size(), tokens.data(), count, true, true) != count)
            throw std::runtime_error("tokenization_failed");
        auto started = std::chrono::steady_clock::now();
        // Cache only the fixed instruction prefix, never any user text. This
        // avoids repeating the expensive prefill on every sentence. Restore a
        // complete sequence snapshot: recurrent models cannot truncate KV like
        // an attention-only model. No state is persisted to disk.
        const std::string boundary = "<|im_start|>user\n";
        auto end = prompt.find(boundary);
        std::vector<llama_token> currentPrefix;
        if (end != std::string::npos) {
            auto prefix = prompt.substr(0, end + boundary.size());
            int n = -api.llama_tokenize(vocab, prefix.data(), (int)prefix.size(), nullptr, 0, true, true);
            if (n > 0 && n < count) {
                currentPrefix.resize(n);
                api.llama_tokenize(vocab, prefix.data(), (int)prefix.size(), currentPrefix.data(), n, true, true);
                int same = 0;
                while (same < n && currentPrefix[same] == tokens[same]) ++same;
                currentPrefix.resize(same);
            }
        }
        bool reused = !prefixState.empty() && currentPrefix == prefixTokens;
        if (reused && api.llama_state_seq_set_data(context, prefixState.data(), prefixState.size(), 0) == 0) {
            api.llama_memory_clear(api.llama_get_memory(context), true);
            reused = false;
        }
        const int prefixCount = (int)currentPrefix.size();
        if (!reused) {
            prefixState.clear(); prefixTokens = currentPrefix;
            for (int start = 0; start < prefixCount; start += 512)
                Decode(tokens.data() + start, std::min(512, prefixCount - start), start);
            if (prefixCount) {
                auto size = api.llama_state_seq_get_size(context, 0);
                if (size <= 128 * 1024 * 1024) {
                    prefixState.resize(size);
                    if (api.llama_state_seq_get_data(context, prefixState.data(), size, 0) != size)
                        prefixState.clear();
                }
            }
        }
        if (request.value("op", "translate") == "prepare")
            return {{"ready", true}, {"prefixCacheBytes", prefixState.size()}, {"cpuThreads", 4}, {"promptThreads", promptThreads}};
        for (int start = prefixCount; start < count; start += 512)
            Decode(tokens.data() + start, std::min(512, count - start), start);
        std::string output;
        bool ended = false;
        int generated = 0;
        for (; generated < limit; ++generated) {
            auto token = api.llama_sampler_sample(sampler, context, -1);
            if (api.llama_vocab_is_eog(vocab, token)) { ended = true; break; }
            std::vector<char> piece(256);
            int n = api.llama_token_to_piece(vocab, token, piece.data(), (int)piece.size(), 0, true);
            if (n < 0) {
                piece.resize(-n);
                n = api.llama_token_to_piece(vocab, token, piece.data(), (int)piece.size(), 0, true);
            }
            if (n < 0) throw std::runtime_error("decode_failed");
            output.append(piece.data(), n);
            if (output.size() > 32768) throw std::runtime_error("output_length");
            Decode(&token, 1, count + generated);
        }
        api.llama_memory_clear(api.llama_get_memory(context), true);
        if (!ended) throw std::runtime_error("generation_incomplete");
        double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
        return { {"text", output}, {"seconds", seconds}, {"inputTokens", count}, {"outputTokens", generated},
                 {"prefixReused", reused}, {"prefixCacheBytes", prefixState.size()} };
    }
};

int wmain(int argc, wchar_t** argv) {
    _setmode(_fileno(stdin), _O_BINARY); _setmode(_fileno(stdout), _O_BINARY);
    std::ios::sync_with_stdio(false);
    try {
        if (argc != 2) throw std::runtime_error("model_argument_required");
        wchar_t executable[32768]{};
        GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
        auto directory = std::filesystem::path(executable).parent_path();
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS);
        AddDllDirectory(directory.c_str());
        auto library = LoadLibraryExW((directory / L"llama.dll").c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        auto ggml = LoadLibraryExW((directory / L"ggml.dll").c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        auto base = LoadLibraryExW((directory / L"ggml-base.dll").c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!library || !ggml || !base) throw std::runtime_error("runtime_load_failed");
        Symbol<decltype(&ggml_log_set)>(base, "ggml_log_set")([](ggml_log_level, const char*, void*) {}, nullptr);
        Symbol<decltype(&ggml_backend_load_all_from_path)>(ggml, "ggml_backend_load_all_from_path")(Utf8(directory.wstring()).c_str());
        Engine engine(library, Utf8(std::filesystem::absolute(argv[1]).wstring()));
        std::string line;
        while (std::getline(std::cin, line)) {
            json result;
            try {
                if (line.size() > 65536) throw std::runtime_error("request_length");
                result = engine.Run(json::parse(line));
            } catch (const std::runtime_error& error) {
                result = {{"error", error.what()}};
            } catch (...) { result = {{"error", "invalid_request"}}; }
            std::cout << result.dump() << '\n' << std::flush;
        }
        return 0;
    } catch (const std::runtime_error& error) {
        std::cout << json({{"error", error.what()}}).dump() << '\n' << std::flush;
    } catch (...) { std::cout << "{\"error\":\"runtime_failed\"}\n" << std::flush; }
    return 1;
}

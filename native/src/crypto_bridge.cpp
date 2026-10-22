#include "save_writer.h"
#include <cstring>

// Expose the qualified game cipher without another implementation or private library APIs.
extern "C" __declspec(dllexport) int parc_chacha20(const uint8_t* key, const uint8_t* nonce,
        const uint8_t* input, uint32_t size, uint8_t* output) {
    if (!key || !nonce || !input || !output) return -1;
    try {
        const auto bytes = SaveWriter::ChaCha20Crypt(
            std::vector<uint8_t>(key, key + 32), std::vector<uint8_t>(nonce, nonce + 16),
            std::vector<uint8_t>(input, input + size));
        std::memcpy(output, bytes.data(), bytes.size());
        return 0;
    } catch (...) { return -2; }
}

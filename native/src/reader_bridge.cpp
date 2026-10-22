#include "save_parser_cpp.h"
#include <cstdlib>
#include <cstring>
#include <limits>
#include <stdexcept>

extern "C" __declspec(dllexport) void parc_reader_free(void* output) { std::free(output); }

static void FindItems(std::vector<SaveParserCpp::GenericFieldValue>& fields, const uint8_t* data,
        uint32_t size, uint64_t number, uint32_t key, std::vector<SaveParserCpp::GenericFieldValue>& matches) {
    for (auto& field : fields) {
        if (SaveParserCpp::MatchesItem(field, data, size, number, key)) matches.push_back(field);
        FindItems(field.child_fields, data, size, number, key, matches);
        FindItems(field.list_elements, data, size, number, key, matches);
    }
}

static int Read(const uint8_t* data, uint32_t size, uint32_t class_index,
        uint64_t number, uint32_t key, void** output, uint32_t* length, const uint32_t* spans = nullptr, uint32_t span_count = 0, bool shallow = false) {
    if (!output || !length) return -1;
    *output = nullptr; *length = 0;
    std::string text;
    int status = 0;
    try {
        if (!data || !size || class_index == UINT32_MAX) throw std::runtime_error("Expected a selected save block");
        if (span_count && (!spans || span_count % 3 || span_count > 4096)) throw std::runtime_error("Invalid selected list spans");
        auto result = span_count
            ? SaveParserCpp::ParseRecordListsBytes(std::vector<uint8_t>(data, data + size), class_index, std::vector<uint32_t>(spans, spans + span_count), shallow)
            : SaveParserCpp::ParseCompactBytes(std::vector<uint8_t>(data, data + size), class_index, key == 0, number, key);
        for (auto& object : result.objects) {
            if (key) {
                std::vector<SaveParserCpp::GenericFieldValue> matches;
                FindItems(object.fields, data, size, number, key, matches);
                object.fields = std::move(matches);
            }
        }
        text = SaveParserCpp::ToRecords(result);
    } catch (const std::exception& error) { status = -1; text = error.what(); }
      catch (...) { return -2; }
    if (text.size() > std::numeric_limits<uint32_t>::max()) return -2;
    *output = std::malloc(text.size());
    if (!*output) return -2;
    std::memcpy(*output, text.data(), text.size());
    *length = static_cast<uint32_t>(text.size());
    return status;
}

extern "C" __declspec(dllexport) int parc_read_block_v2(const uint8_t* data, uint32_t size,
        uint32_t class_index, void** output, uint32_t* length) {
    return Read(data, size, class_index, 0, 0, output, length);
}

extern "C" __declspec(dllexport) int parc_read_lists_v2(const uint8_t* data, uint32_t size,
        uint32_t class_index, const uint32_t* spans, uint32_t span_count, uint32_t shallow, void** output, uint32_t* length) {
    if (shallow > 1) {
        if (output) *output = nullptr;
        if (length) *length = 0;
        return -1;
    }
    return Read(data, size, class_index, 0, 0, output, length, spans, span_count, shallow != 0);
}

extern "C" __declspec(dllexport) int parc_read_item_references_v2(const uint8_t* data, uint32_t size,
        uint32_t class_index, uint64_t number, uint32_t key, void** output, uint32_t* length) {
    return Read(data, size, class_index, number, key, output, length);
}

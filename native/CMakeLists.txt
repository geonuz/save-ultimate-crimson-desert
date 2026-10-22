cmake_minimum_required(VERSION 3.24)
project(SaveUltimateNative LANGUAGES C CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_library(parc_reader SHARED src/reader_bridge.cpp src/save_parser_cpp.cpp)
target_compile_definitions(parc_reader PRIVATE SAVEULTIMATE_SELECTED_READER)
target_link_libraries(parc_reader PRIVATE bcrypt)
set_target_properties(parc_reader PROPERTIES PREFIX "" OUTPUT_NAME "parc_reader")
if(MINGW)
    target_link_options(parc_reader PRIVATE -static-libgcc -static-libstdc++)
elseif(MSVC)
    target_compile_options(parc_reader PRIVATE /utf-8)
endif()

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "parc_parser requires a Windows x64 toolchain")
endif()

# Indexing and validation share the decoder; the original DLL remains an oracle.
add_library(lz4_lib STATIC third_party/lz4/lz4.c third_party/lz4/lz4hc.c)
target_include_directories(lz4_lib PUBLIC third_party/lz4)

add_library(parc_parser SHARED
    src/parc_dll.cpp
    src/save_parser_cpp.cpp
    src/save_writer.cpp
    src/parc_serializer.cpp
    src/parc_engine.cpp
)
target_include_directories(parc_parser PRIVATE src)
target_link_libraries(parc_parser PRIVATE bcrypt lz4_lib)
set_target_properties(parc_parser PROPERTIES PREFIX "" OUTPUT_NAME "parc_parser")

if(MSVC)
    target_compile_options(parc_parser PRIVATE /utf-8)
elseif(MINGW)
    # Keep the DLL independent of the developer's GCC runtime installation.
    target_link_options(parc_parser PRIVATE -static-libgcc -static-libstdc++)
endif()

add_library(parc_index SHARED src/parc_index.cpp src/save_parser_cpp.cpp)
target_compile_definitions(parc_index PRIVATE SAVEULTIMATE_COMPACT_INDEX)
target_link_libraries(parc_index PRIVATE bcrypt)
set_target_properties(parc_index PROPERTIES PREFIX "" OUTPUT_NAME "parc_index")
if(MSVC)
    target_compile_options(parc_index PRIVATE /utf-8)
elseif(MINGW)
    target_link_options(parc_index PRIVATE -static-libgcc -static-libstdc++)
endif()

# Qualified independently against the original DLL before runtime adoption.
add_library(parc_validation SHARED src/parc_dll.cpp src/save_parser_cpp.cpp
    src/save_writer.cpp src/parc_serializer.cpp src/crypto_bridge.cpp)
target_compile_definitions(parc_validation PRIVATE SAVEULTIMATE_STREAM_VALIDATION)
target_include_directories(parc_validation PRIVATE src)
target_link_libraries(parc_validation PRIVATE bcrypt lz4_lib)
set_target_properties(parc_validation PROPERTIES PREFIX "" OUTPUT_NAME "parc_validation")
if(MINGW)
    target_link_options(parc_validation PRIVATE -static-libgcc -static-libstdc++)
elseif(MSVC)
    target_compile_options(parc_validation PRIVATE /utf-8)
endif()


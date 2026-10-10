cmake_minimum_required(VERSION 3.25)

# Lustral's version is set once, in Directory.Build.props, so that the proxy's version resource and messages match the
# release.
set(lustral_build_props "${CMAKE_CURRENT_LIST_DIR}/../Directory.Build.props")
file(READ "${lustral_build_props}" lustral_build_props_content)
if(NOT lustral_build_props_content MATCHES "<Version>(([0-9]+)\\.([0-9]+)\\.([0-9]+)[^<]*)</Version>")
    message(FATAL_ERROR "Directory.Build.props has no <Version>")
endif()
set(LUSTRAL_VERSION "${CMAKE_MATCH_1}")
project(LustralStage0 VERSION ${CMAKE_MATCH_2}.${CMAKE_MATCH_3}.${CMAKE_MATCH_4} LANGUAGES CXX RC)
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${lustral_build_props}")
configure_file(src/version.h.in "${CMAKE_CURRENT_BINARY_DIR}/generated/version.h" @ONLY)

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "Stage-0 targets 64-bit Windows for now; Linux arrives in M3.")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Static CRT, so that the proxy does not depend on an installed Visual C++ redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# No exceptions or RTTI: stage 0 runs before any runtime exists and must remain small and predictable.
string(REPLACE "/EHsc" "" CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS}")
string(REPLACE "/GR" "" CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS}")
add_compile_definitions(UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _HAS_EXCEPTIONS=0)
add_compile_options(/W4 /WX /permissive- /utf-8 /Zc:__cplusplus /GR- /EHs-c- /Zi)
add_link_options(/DEBUG /OPT:REF /OPT:ICF)

add_library(lustral_stage0 STATIC
    src/stage0.cpp
    src/log.cpp
    src/pe.cpp
    src/coreclr_host.cpp
    src/mono_host.cpp)
target_include_directories(lustral_stage0 PUBLIC src "${CMAKE_CURRENT_BINARY_DIR}/generated")

# The proxy, named after the Windows DLL it replaces. winhttp.dll is imported by UnityPlayer.dll in all Unity versions
# that Lustral supports.
add_library(winhttp SHARED src/dllmain.cpp src/version.rc)
target_link_libraries(winhttp PRIVATE lustral_stage0)

# Tests stage 0 without a game; see test/stage0_test.cpp.
add_executable(stage0_test test/stage0_test.cpp)
target_link_libraries(stage0_test PRIVATE lustral_stage0)

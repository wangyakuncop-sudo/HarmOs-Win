cmake_minimum_required(VERSION 3.5.0)
project(harmoscap)

set(NATIVERENDER_ROOT_PATH ${CMAKE_CURRENT_SOURCE_DIR})

if(DEFINED PACKAGE_FIND_FILE)
    include(${PACKAGE_FIND_FILE})
endif()

include_directories(${NATIVERENDER_ROOT_PATH})

add_library(harmoscap SHARED harmoscap.cpp)

target_link_libraries(harmoscap PUBLIC
    libace_napi.z.so
    libnative_avscreen_capture.so
    libnative_media_venc.so
    libnative_media_codecbase.so
    libnative_media_core.so
    libhilog_ndk.z.so)

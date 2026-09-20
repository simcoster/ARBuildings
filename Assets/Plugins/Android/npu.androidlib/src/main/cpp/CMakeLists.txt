cmake_minimum_required(VERSION 3.22.1)
project(npu_gl)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT LITERT_JNI_DIR)
  message(FATAL_ERROR "LITERT_JNI_DIR must be set to the extracted LiteRT jni folder")
endif()

add_library(npu_gl SHARED npu_gl_segmenter.cpp)

target_include_directories(npu_gl PRIVATE
  ${CMAKE_CURRENT_SOURCE_DIR}
  ${CMAKE_CURRENT_SOURCE_DIR}/third_party)

add_library(LiteRt SHARED IMPORTED)
set_target_properties(LiteRt PROPERTIES
  IMPORTED_LOCATION "${LITERT_JNI_DIR}/${ANDROID_ABI}/libLiteRt.so")

find_library(log-lib log)
find_library(egl-lib EGL)
find_library(gles-lib GLESv3)
find_library(android-lib android)

target_link_libraries(npu_gl
  LiteRt
  ${gles-lib}
  ${egl-lib}
  ${log-lib}
  ${android-lib}
  c++_shared)

target_compile_options(npu_gl PRIVATE -fvisibility=hidden -Wall -Wextra -Wno-unused-parameter)

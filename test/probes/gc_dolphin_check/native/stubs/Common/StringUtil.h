// Stand-in for Dolphin's Common/StringUtil.h: the one conversion
// LibusbUtils::GetStringDescriptor calls.
#pragma once

#include <string>
#include <string_view>

std::string UTF16ToUTF8(std::u16string_view input);

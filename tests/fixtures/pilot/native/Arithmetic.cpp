#include "Arithmetic.hpp"

// Byte-span fixture: café 🚀. This text precedes the definitions.
namespace pilot {
int Add(int left, int right) { return left + right; }
long long Add(long long left, long long right) { return left + right; }

__declspec(noinline) int Choose(int value) {
    int result;
    if (value > 0)
        result = Add(value, 1);
    else
        result = 0;
    return result;
}

int Unused(int value) { return value - 1; }
}

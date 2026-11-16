#include <iostream>
#include <numeric>
#include <vector>

int main() {
    const std::vector<int> values{1, 2, 3};
    if (std::accumulate(values.begin(), values.end(), 0) != 6) return 1;
    std::cout << "Native compile/link/run passed; MSVC=" << _MSC_FULL_VER << '\n';
    return 0;
}

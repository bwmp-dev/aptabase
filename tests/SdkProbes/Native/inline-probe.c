static __attribute__((always_inline)) inline int native_leaf(volatile int *value) {
    return *value + 3;
}
static __attribute__((always_inline)) inline int native_middle(volatile int *value) {
    return native_leaf(value) * 2;
}
__attribute__((noinline)) int aptabase_inline_probe(volatile int *value) {
    return native_middle(value) + 1;
}

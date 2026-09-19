// SharpMetal Frontier 5: Self-Test POSIX / Libc Compatibility Binary
// Freestanding static PIE ELF using Linux x86-64 ABI syscalls

#define SYS_read  0
#define SYS_write 1
#define SYS_exit  60

static inline long syscall3(long num, long a1, long a2, long a3)
{
    long ret;
    register long r10 __asm__("r10") = 0;
    __asm__ volatile (
        "syscall"
        : "=a"(ret)
        : "a"(num), "D"(a1), "S"(a2), "d"(a3), "r"(r10)
        : "rcx", "r11", "memory"
    );
    return ret;
}

static inline void sys_exit(int code)
{
    __asm__ volatile (
        "syscall"
        :
        : "a"((long)SYS_exit), "D"((long)code)
        : "rcx", "r11", "memory"
    );
}

static const char msg_hello[] = "Hello from POSIX\n";
static const char msg_pass[] = "[PASS] POSIX: SYS_write and SYS_read executed successfully\n";

void _start(void)
{
    // 1. SYS_write(1, "Hello from POSIX\n", 17)
    syscall3(SYS_write, 1, (long)msg_hello, sizeof(msg_hello) - 1);

    // 2. Blocking read test: posix_test issues SYS_read(0)
    // Kernel yields in Ring 0 until a key is supplied by harness without deadlocking
    char c = 0;
    while (1)
    {
        long r = syscall3(SYS_read, 0, (long)&c, 1);
        if (r > 0 && c != 0)
        {
            break;
        }
    }

    // 3. SYS_write pass message
    syscall3(SYS_write, 1, (long)msg_pass, sizeof(msg_pass) - 1);

    // 4. SYS_exit(0)
    sys_exit(0);
}

// SharpMetal Frontier 2: Self-Test ELF Binary
// Freestanding static PIE ELF using microkernel syscalls

#define SYS_read  0x24
#define SYS_write 0x25
#define SYS_yield 0x01

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

static inline void sys_write(int fd, const char* str, unsigned long len)
{
    syscall3(SYS_write, fd, (long)str, len);
}

static inline long sys_read(int fd, char* buf, unsigned long count)
{
    return syscall3(SYS_read, fd, (long)buf, count);
}

static inline void sys_yield(void)
{
    syscall3(SYS_yield, 0, 0, 0);
}

static const char s_msg[] = "Hello from ELF\n";
const char* const p_msg = s_msg;

void _start(void)
{
    sys_write(1, p_msg, sizeof(s_msg) - 1);

    // Wait for keypress via SYS_read without starving the system
    char c = 0;
    while (1)
    {
        long r = sys_read(0, &c, 1);
        if (r > 0 && c != 0)
        {
            break;
        }
        sys_yield();
    }

    while (1)
    {
        sys_yield();
    }
}

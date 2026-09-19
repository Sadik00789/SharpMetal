// SharpMetal Frontier 5: POSIX Cat Self-Test Binary
// Freestanding static PIE ELF using Linux x86-64 ABI syscalls

#define SYS_read  0
#define SYS_write 1
#define SYS_open  2
#define SYS_close 3
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

static inline long syscall1(long num, long a1)
{
    long ret;
    __asm__ volatile (
        "syscall"
        : "=a"(ret)
        : "a"(num), "D"(a1)
        : "rcx", "r11", "memory"
    );
    return ret;
}

static const char file_path[] = "/HELLO.TXT";

void _start(void)
{
    // 1. SYS_open("/HELLO.TXT", O_RDONLY, 0)
    long fd = syscall3(SYS_open, (long)file_path, 0, 0);
    if (fd >= 0)
    {
        char buf[64];
        long bytes_read;

        // 2. Loop SYS_read and echo to SYS_write(1)
        while ((bytes_read = syscall3(SYS_read, fd, (long)buf, sizeof(buf))) > 0)
        {
            syscall3(SYS_write, 1, (long)buf, bytes_read);
        }

        // Echo newline for visual cleanliness on serial console
        syscall3(SYS_write, 1, (long)"\n", 1);

        // 3. SYS_close(fd)
        syscall1(SYS_close, fd);
    }

    // 4. SYS_exit(0)
    syscall1(SYS_exit, 0);
}

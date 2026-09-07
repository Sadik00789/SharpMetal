; -----------------------------------------------------------------------------
; SharpMetal x86-64 Symmetric Multiprocessing (SMP) AP Trampoline
; Positioned at physical address 0x0000_8000 (SIPI Vector 0x08)
; Transitions AP from 16-bit Real Mode -> 32-bit Protected Mode -> 64-bit Long Mode
; -----------------------------------------------------------------------------
[BITS 16]
[ORG 0x8000]
default abs

ap_trampoline_start:
    jmp short trampoline_code
    nop

align 8
global trampoline_pml4
trampoline_pml4:
    dq 0

align 8
global trampoline_entry64
trampoline_entry64:
    dq 0

align 8
trampoline_code:
    cli
    cld

    ; Setup flat real-mode segments (base 0)
    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov fs, ax
    mov gs, ax

    ; Load temporary 32-bit GDT
    lgdt [trampoline_gdt_ptr]

    ; Enable Protected Mode (CR0.PE = 1)
    mov eax, cr0
    or eax, 1
    mov cr0, eax

    ; Far jump to 32-bit Compatibility entry point (CS = 0x08)
    jmp dword 0x08:prot32_entry

[BITS 32]
prot32_entry:
    ; Setup 32-bit protected-mode data segments (selector 0x10)
    mov ax, 0x10
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov fs, ax
    mov gs, ax

    ; Enable Physical Address Extension (CR4.PAE = 1, bit 5)
    mov eax, cr4
    or eax, (1 << 5)
    mov cr4, eax

    ; Set Long Mode Enable (LME = 1, bit 8) in IA32_EFER MSR (0xC0000080)
    mov ecx, 0xC0000080
    rdmsr
    or eax, (1 << 8)
    wrmsr

    ; Load CR3 with the kernel's active PML4 physical address
    mov eax, [trampoline_pml4]
    mov cr3, eax

    ; Enable Paging (CR0.PG = 1, bit 31) and Protection (CR0.PE = 1)
    mov eax, cr0
    or eax, 0x80000001
    mov cr0, eax

    ; Far jump into 64-bit Long Mode code segment (selector 0x18)
    jmp 0x18:long64_entry

[BITS 64]
long64_entry:
    ; Reload CR3 with full 64-bit PML4 in Long Mode
    mov rax, [trampoline_pml4]
    mov cr3, rax

    ; Jump to canonical higher-half AP entry thunk (ApEntry64)
    mov rax, [trampoline_entry64]
    jmp rax

align 16
trampoline_gdt:
    dq 0x0000000000000000   ; 0x00: Null Descriptor
    dq 0x00CF9A000000FFFF   ; 0x08: 32-bit Code (DPL 0, Exec/Read, G=1, D=1)
    dq 0x00CF92000000FFFF   ; 0x10: 32-bit Data (DPL 0, Read/Write, G=1, D=1)
    dq 0x00209A0000000000   ; 0x18: 64-bit Code (DPL 0, Exec/Read, L=1, D=0)
trampoline_gdt_end:

align 4
trampoline_gdt_ptr:
    dw trampoline_gdt_end - trampoline_gdt - 1
    dd trampoline_gdt

align 16
ap_trampoline_end:

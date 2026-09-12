; AUDIT HARDENING (v1.1.0): Ring 0 -> Ring 3 Register Hygiene & Strict System V ABI
; DropToUser / EnterUserMode / UserThreadTrampoline never return to the kernel caller
; (iretq drops CPL 0->3), so callee-saved regs need no restore - but they
; MUST NOT leak kernel contents to userland. All non-argument volatile and
; callee-saved GPRs are explicitly zeroed before iretq. Segment registers
; use user selectors (DS=ES=FS=0x1B); GS base is preserved (per-CPU data).
default rel
section .text

global DropToUser
global EnterUserMode
global UserThreadTrampoline
global GetUserThreadTrampoline

; -----------------------------------------------------------------------------
; void DropToUser(ulong userRip, ulong userRsp, ulong cr3)
; Enters Ring 3 (CPL = 3) deterministically via iretq.
; Enforces strict System V AMD64 ABI:
;   RDI = Target Userland Entry Point (User RIP)
;   RSI = Target Userland Stack Pointer (User RSP)
;   RDX = Target Address Space PML4/CR3 (if switching page tables)
; -----------------------------------------------------------------------------
DropToUser:
    ; 1. If switching page tables:
    test rdx, rdx
    jz .skip_cr3
    mov cr3, rdx
.skip_cr3:

    ; 2. Disable interrupts during transition setup
    cli

    ; 3. Reload data segment registers with User DS (0x18 | 3 = 0x1B)
    mov ax, 0x1B
    mov ds, ax
    mov es, ax
    mov fs, ax
    ; Do not write to gs: writing to gs resets IA32_GS_BASE to 0!

    ; 4. Build the 5-QWORD iretq stack frame in exact architectural order:
    ; [rsp + 32] = User SS:   0x1B (User Data Selector 0x18 | 3)
    ; [rsp + 24] = User RSP:  rsi (User Stack Top from RSI)
    ; [rsp + 16] = RFLAGS:    0x3202 (IF = 1 enabled, IOPL = 3)
    ; [rsp + 8]  = User CS:   0x23 (User Code Selector 0x20 | 3)
    ; [rsp + 0]  = User RIP:  rdi (User Entry Point from RDI)
    push qword 0x1B
    push rsi
    push qword 0x3202
    push qword 0x23
    push rdi

    ; 5. Scrub kernel register state before exposing Ring 3 context
    xor rax, rax
    xor rbx, rbx
    xor rcx, rcx
    xor rdx, rdx
    xor rsi, rsi
    xor rdi, rdi
    xor rbp, rbp
    xor r8, r8
    xor r9, r9
    xor r10, r10
    xor r11, r11
    xor r12, r12
    xor r13, r13
    xor r14, r14
    xor r15, r15

    ; 6. Execute iretq to drop CPL from 0 to 3
    iretq

; -----------------------------------------------------------------------------
; void EnterUserMode(ulong userRip, ulong userRsp, ulong cr3)
; Static Win64 ABI entry point (RCX=rip, RDX=rsp, R8=cr3) mapping to System V ABI:
; -----------------------------------------------------------------------------
EnterUserMode:
    mov rdi, rcx
    mov rsi, rdx
    mov rdx, r8
    jmp DropToUser

; -----------------------------------------------------------------------------
; User Thread Start Trampoline
; Switched into via ContextSwitch ret for newly synthesized user threads.
; -----------------------------------------------------------------------------
extern ReleaseSchedulerLock

UserThreadTrampoline:
    ; Release scheduler spinlock acquired during context switch
    sub rsp, 32
    mov rcx, 0x202
    mov rdi, 0x202
    call ReleaseSchedulerLock
    add rsp, 32

    cli

    ; 1. Scrub kernel GPRs before Ring 3 entry (no kernel data leak).
    xor rax, rax
    xor rbx, rbx
    xor rcx, rcx
    xor rdx, rdx
    xor rsi, rsi
    xor rdi, rdi
    xor rbp, rbp
    xor r8, r8
    xor r9, r9
    xor r10, r10
    xor r11, r11
    xor r12, r12
    xor r13, r13
    xor r14, r14
    xor r15, r15

    ; 2. Load User Data segment selector into segment registers
    mov ax, 0x1B
    mov ds, ax
    mov es, ax
    mov fs, ax
    ; Do not write to gs: writing to gs resets IA32_GS_BASE to 0!

    ; 3. Stack currently holds 5 iretq qwords (RIP, CS, RFLAGS, RSP, SS)
    ; Pop iretq frame and drop CPL from 0 to 3
    iretq

; -----------------------------------------------------------------------------
; ulong GetUserThreadTrampoline()
; Returns the address of UserThreadTrampoline for synthetic user stack setup
; -----------------------------------------------------------------------------
GetUserThreadTrampoline:
    lea rax, [rel UserThreadTrampoline]
    ret

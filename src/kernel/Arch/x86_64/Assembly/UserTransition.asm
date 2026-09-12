; AUDIT HARDENING (v1.0.2): Ring 0 -> Ring 3 Register Hygiene
; EnterUserMode / UserThreadTrampoline never return to the kernel caller
; (iretq drops CPL 0->3), so callee-saved regs need no restore - but they
; MUST NOT leak kernel contents to userland. All non-argument volatile and
; callee-saved GPRs are explicitly zeroed before iretq. Segment registers
; use user selectors (DS=ES=FS=0x1B); GS base is preserved (per-CPU data).
default rel
section .text

global EnterUserMode
global UserThreadTrampoline
global GetUserThreadTrampoline

; -----------------------------------------------------------------------------
; void EnterUserMode(ulong entryRip, ulong userRsp, ulong pml4Phys)
; Enters Ring 3 (CPL = 3) deterministically via iretq.
; Input (Microsoft x64 ABI):
; rcx = entryRip (e.g. 0x40001000)
; rdx = userRsp (16-byte aligned)
; r8  = pml4Phys
; -----------------------------------------------------------------------------
EnterUserMode:
    ; 1. Load CR3 with user process PML4 (which mirrors kernel high-half)
    mov cr3, r8

    ; 2. Load User Data segment selector (0x18 | 3 = 0x1B) into data segment registers
    mov ax, 0x1B
    mov ds, ax
    mov es, ax
    mov fs, ax
    ; Do not write to gs: writing to gs resets IA32_GS_BASE to 0!

    ; 3. Scrub kernel register state before exposing Ring 3 context.
    ; rcx/rdx/r8 hold entry args (consumed below); everything else that
    ; could leak kernel data (rax, rbx, rbp, rsi, rdi, r9-r15) is zeroed.
    ; Callee-saved set (rbx, rbp, r12-r15) is therefore never leaked and
    ; AOT caller frames cannot observe stale kernel values in userland.
    xor rax, rax
    xor rbx, rbx
    xor rbp, rbp
    xor rsi, rsi
    xor rdi, rdi
    xor r9, r9
    xor r10, r10
    xor r11, r11
    xor r12, r12
    xor r13, r13
    xor r14, r14
    xor r15, r15

    ; 4. Push iretq frame (5 qwords):
    ; [rsp + 32] = User SS:   0x1B (User Data Selector 0x18 | 3)
    ; [rsp + 24] = User RSP:  rdx (16-byte aligned)
    ; [rsp + 16] = RFLAGS:    0x3202 (IF = 1 enabled, IOPL = 3)
    ; [rsp + 8]  = User CS:   0x23 (User Code Selector 0x20 | 3)
    ; [rsp + 0]  = User RIP:  rcx (entryRip)
    push 0x1B
    push rdx
    push 0x3202
    push 0x23
    push rcx

    ; 5. Execute iretq to drop CPL from 0 to 3
    iretq

; -----------------------------------------------------------------------------
; User Thread Start Trampoline
; Switched into via ContextSwitch ret for newly synthesized user threads.
; -----------------------------------------------------------------------------
extern ReleaseSchedulerLock

UserThreadTrampoline:
    ; Release scheduler spinlock acquired during context switch
    sub rsp, 32
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

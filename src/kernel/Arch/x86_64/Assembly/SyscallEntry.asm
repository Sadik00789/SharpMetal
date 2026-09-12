default rel
section .text

global SyscallEntry
global DoSyscall
global GetSyscallEntry
global SetSyscallKernelRsp

extern DispatchSyscall

; -----------------------------------------------------------------------------
; Hardware SYSCALL Entry Point (IA32_LSTAR target)
; Configured in LSTAR; hardware transitions to this point upon 'syscall'.
; -----------------------------------------------------------------------------
SyscallEntry:
    ; 1. Atomic Stack Swap via per-CPU GS base:
    ; gs:[24] = UserRspScratch
    ; gs:[16] = KernelRsp
    mov [gs:24], rsp
    mov rsp, [gs:16]

    ; 2. Preserve user context on the kernel stack FIRST (Phase 2a):
    ; AMD64 syscall HW overwrites RCX=user RIP and R11=user RFLAGS, so the
    ; FastPath IPC return must never use RCX/R11 for payload. Spill them
    ; immediately into per-CPU kernel-stack scratch before the dispatcher
    ; can clobber anything.
    push qword [gs:24]    ; [rsp] user RSP scratch
    push rcx              ; [rsp+8] user RIP (HW-saved)
    push r11              ; [rsp+16] user RFLAGS (HW-saved)

    ; 3. Preserve ALL caller-saved user registers that sysretq does NOT
    ; restore: RAX(num) is return channel, but RDI/RSI/RDX/R10/R8/R9 may
    ; carry user args that C# dispatch (Win64 RCX/RDX/R8/R9) clobbers.
    push rdx              ; user a3
    push r10              ; user a4
    push r8               ; user r8 payload reg
    push r9               ; user r9 payload reg

    ; 4. Preserve callee-saved registers
    push rbx
    push rbp
    push r12
    push r13
    push r14
    push r15
    push rdi
    push rsi

    ; Total pushes = 3 (UserRsp, RCX, R11) + 4 (RDX,R10,R8,R9) + 8 (GPRs) = 15 pushes = 120 bytes.
    ; (120 + 72) = 192 bytes (0 mod 16).
    ; 72 bytes allocated on stack:
    ; [rsp + 0]..[rsp + 31] = 32-byte shadow space for Microsoft x64 DispatchSyscall
    ; [rsp + 32] = 5th argument: a4
    ; [rsp + 40] = 6th argument: a5 (d2)
    ; [rsp + 48] = 7th argument: a6 (d3)
    ; [rsp + 56]..[rsp + 71] = 16 bytes alignment padding
    sub rsp, 72

    ; Map arguments to Microsoft x64 calling convention:
    ; DispatchSyscall(rcx=num, rdx=a1, r8=a2, r9=a3, [rsp+32]=a4, [rsp+40]=a5, [rsp+48]=a6)
    ; Input: rax=num, rdi=a1, rsi=a2, rdx=a3, r10=a4, r12=a5, r13=a6
    ; (push preserves stack copies; source registers still hold user values)
    mov [rsp + 32], r10     ; 5th arg: a4
    mov [rsp + 40], r12     ; 6th arg: a5 (d2)
    mov [rsp + 48], r13     ; 7th arg: a6 (d3)
    mov r9, rdx             ; 4th arg: a3
    mov r8, rsi             ; 3rd arg: a2
    mov rdx, rdi            ; 2nd arg: a1
    mov rcx, rax            ; 1st arg: num

    ; 4. Dispatch to C# handler
    call DispatchSyscall

    ; Disable interrupts before popping registers and restoring user RSP
    cli

    add rsp, 72

    ; 5. Restore callee-saved + user payload registers (NOT RCX/R11 yet)
    pop rsi
    pop rdi
    pop r15
    pop r14
    pop r13
    pop r12
    pop rbp
    pop rbx
    pop r9
    pop r8
    pop r10
    pop rdx

    ; 6. FastPath IPC rendezvous exit: RCX/R11 are RESERVED for sysretq.
    ; Return value travels only in RAX (set by DispatchSyscall). RDI, RSI,
    ; RDX, R8, R9, R10 above are restored verbatim so user payload survives.
    pop r11               ; target RFLAGS -> R11
    pop rcx               ; target RIP    -> RCX

    ; 7. Restore user RSP
    pop rsp

    ; 8. Return to Ring 3 (sysretq restores CS=0x23, SS=0x1B, CPL=3, RIP=RCX, RFLAGS=R11)
    o64 sysret

; -----------------------------------------------------------------------------
; Safe Syscall Dispatch Verification Helper for Phase 4 / Phase 5
; ulong DoSyscall(ulong num, ulong a1, ulong a2, ulong a3, ulong a4, ulong a5, ulong a6)
; Validates calling convention, argument routing, register preservation,
; and C# DispatchSyscall invocation.
; -----------------------------------------------------------------------------
DoSyscall:
    push rbx
    push rbp
    push r12
    push r13
    push r14
    push r15

    sub rsp, 56

    mov rax, [rsp + 144]
    mov [rsp + 32], rax     ; 5th arg: a4

    mov rax, [rsp + 152]
    mov [rsp + 40], rax     ; 6th arg: a5 (d2)

    mov rax, [rsp + 160]
    mov [rsp + 48], rax     ; 7th arg: a6 (d3)

    call DispatchSyscall

    add rsp, 56

    pop r15
    pop r14
    pop r13
    pop r12
    pop rbp
    pop rbx
    ret

; -----------------------------------------------------------------------------
; ulong GetSyscallEntry()
; Returns the 64-bit virtual address of SyscallEntry for IA32_LSTAR MSR
; -----------------------------------------------------------------------------
GetSyscallEntry:
    lea rax, [rel SyscallEntry]
    ret

; -----------------------------------------------------------------------------
; void SetSyscallKernelRsp(ulong rsp0)
; Sets the per-CPU kernel stack pointer to switch to upon syscall from Ring 3
; -----------------------------------------------------------------------------
SetSyscallKernelRsp:
    mov [gs:16], rcx
    ret

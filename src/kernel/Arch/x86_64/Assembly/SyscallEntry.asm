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
    ; 1. Atomic Stack Swap:
    ; Save incoming user RSP to per-CPU scratch variable and switch to KernelStackTop
    mov [rel SyscallUserRsp], rsp
    mov rsp, [rel SyscallKernelRsp]

    ; 2. Preserve user context on the kernel stack:
    ; Hardware saves user RIP -> RCX, user RFLAGS -> R11
    push qword [rel SyscallUserRsp]
    push rcx
    push r11

    ; 3. Preserve callee-saved registers
    push rbx
    push rbp
    push r12
    push r13
    push r14
    push r15
    push rdi
    push rsi

    ; Total pushes = 3 (UserRsp, RCX, R11) + 8 (GPRs) = 11 pushes = 88 bytes.
    ; (88 + 72) = 160 bytes (0 mod 16).
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
    mov [rsp + 32], r10     ; 5th arg: a4
    mov [rsp + 40], r12     ; 6th arg: a5 (d2)
    mov [rsp + 48], r13     ; 7th arg: a6 (d3)
    mov r9, rdx             ; 4th arg: a3
    mov r8, rsi             ; 3rd arg: a2
    mov rdx, rdi            ; 2nd arg: a1
    mov rcx, rax            ; 1st arg: num

    ; 4. Dispatch to C# handler
    call DispatchSyscall

    add rsp, 72

    ; 5. Restore callee-saved registers
    pop rsi
    pop rdi
    pop r15
    pop r14
    pop r13
    pop r12
    pop rbp
    pop rbx

    ; 6. Explicitly restore user RFLAGS and user RIP (Constraint 4)
    pop r11
    pop rcx

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
; Sets the kernel stack pointer to switch to upon syscall from Ring 3
; -----------------------------------------------------------------------------
SetSyscallKernelRsp:
    mov [rel SyscallKernelRsp], rcx
    ret

section .data
global SyscallUserRsp
global SyscallKernelRsp
align 16
SyscallUserRsp:   dq 0
SyscallKernelRsp: dq 0

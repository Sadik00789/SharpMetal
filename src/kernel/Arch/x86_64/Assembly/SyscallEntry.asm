; AUDIT HARDENING (v1.0.2): Syscall / Fastpath Register Preservation
; All Win64 + SysV callee-saved registers (rbx, rbp, r12, r13, r14, r15)
; plus rdi/rsi payload regs are explicitly spilled on entry and restored
; verbatim before sysretq, so Native AOT register-state assumptions survive
; Ring 0 <-> Ring 3 roundtrips. RCX/R11 are RESERVED for sysretq (HW-saved
; RIP/RFLAGS) and never carry IPC payload. Interrupts are masked (cli)
; before the restore path to close the preemption window.
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
    ; 1. Atomic Stack Swap via per-CPU GS base (no GS swap: GS_BASE is per-CPU, never reloaded):
    ; gs:[24] = UserRspScratch, gs:[16] = KernelRsp. IA32_FMASK=0x200 masks IF on entry
    ; and `cli` before restore closes the preemption window. NMI (#2) cannot be masked
    ; and uses IST1 (see Idt.Initialize), so it never runs on this half-swapped RSP.
    mov [gs:24], rsp
    mov rsp, [gs:16]
    and rsp, -16

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

    ; 4. Preserve ALL callee-saved registers (Win64 non-volatile set):
    ; rbx, rbp, r12-r15 + rdi/rsi (AOT codegen spills these; clobbering
    ; them across Ring 0<->Ring 3 would corrupt caller frames).
    push rbx
    push rbp
    push r12
    push r13
    push r14
    push r15
    push rdi
    push rsi

    ; Preserve frame base and syscall number in callee-saved registers before XSAVE.
    mov rbx, rsp            ; rbx = base of pushed GPR frame
    mov r14, rax            ; r14 = syscall number

    ; Save FPU/SSE/AVX state if CR4.OSXSAVE (bit 18) is enabled
    mov rax, cr4
    bt eax, 18
    jnc .no_xsave_sys

    sub rsp, 832
    and rsp, -64
    xor eax, eax
    mov [rsp + 512], rax
    mov [rsp + 520], rax
    mov [rsp + 528], rax
    mov [rsp + 536], rax
    mov [rsp + 544], rax
    mov [rsp + 552], rax
    mov [rsp + 560], rax
    mov [rsp + 568], rax

    mov eax, 7              ; RFBM = x87 | SSE | AVX
    xor edx, edx
    xsave64 [rsp]
    mov r15, rsp            ; r15 = XSAVE area base (preserved by callee-saved Win64)
    sub rsp, 64             ; 32B shadow + 3 stack args + 8B align (0 mod 16)
    jmp .setup_args

.no_xsave_sys:
    xor r15d, r15d
    and rsp, -16
    sub rsp, 64

.setup_args:
    ; Map arguments from saved frame [rbx + ...] to Microsoft x64 calling convention:
    ; [rbx+0]=rsi, [rbx+8]=rdi, [rbx+16]=r15, [rbx+24]=r14, [rbx+32]=r13, [rbx+40]=r12,
    ; [rbx+48]=rbp, [rbx+56]=rbx, [rbx+64]=r9, [rbx+72]=r8, [rbx+80]=r10, [rbx+88]=rdx
    mov rcx, r14            ; 1st arg: num
    mov rax, [rbx + 80]
    mov [rsp + 32], rax     ; 5th arg: a4

    ; Frontier 5: Check calling thread AbiMode (offset 244 in TCB)
    ; If AbiMode == 1 (Linux ABI), 5th arg is user r8, 6th arg is user r9
    mov rax, [gs:8]         ; CurrentThread (PerCpuData offset 8)
    test rax, rax
    jz .default_abi
    cmp dword [rax + 244], 1 ; AbiMode == 1 (Linux ABI)
    jne .default_abi
    mov rax, [rbx + 72]
    mov [rsp + 40], rax     ; 6th arg: Linux a5 (user r8)
    mov rax, [rbx + 64]
    mov [rsp + 48], rax     ; 7th arg: Linux a6 (user r9)
    jmp .dispatch
.default_abi:
    mov rax, [rbx + 40]
    mov [rsp + 40], rax     ; 6th arg: SharpMetal a5 (d2)
    mov rax, [rbx + 32]
    mov [rsp + 48], rax     ; 7th arg: SharpMetal a6 (d3)
.dispatch:
    mov rdx, [rbx + 8]      ; 2nd arg: a1 (user rdi)
    mov r8,  [rbx + 0]      ; 3rd arg: a2 (user rsi)
    mov r9,  [rbx + 88]     ; 4th arg: a3 (user rdx)

    ; 4. Dispatch to C# handler
    call DispatchSyscall

    ; Disable interrupts before popping registers and restoring user RSP
    cli

    test r15, r15
    jz .skip_xrstor_sys
    mov r14, rax            ; preserve syscall return value across xrstor64
    mov eax, 7
    xor edx, edx
    xrstor64 [r15]
    mov rax, r14
.skip_xrstor_sys:
    mov rsp, rbx

    ; Canonical user-half RIP check before sysret (prevents Ring-0 #GP with user RSP active)
    mov rcx, [rsp + 104]
    shr rcx, 47
    jz .rip_canonical
    mov qword [rsp + 104], 0
.rip_canonical:
    ; Sanitize RFLAGS: clear NT (bit 14) and VM (bit 17), ensure IF=1 & bit 1 set
    mov r11, [rsp + 96]
    and r11, ~0x24000
    or  r11, 0x202
    mov [rsp + 96], r11

    ; 5. Restore callee-saved + user payload registers in exact reverse
    ; order (NOT RCX/R11 yet - those are sysretq-reserved).
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

default rel
section .text

global ContextSwitch
global ThreadStartTrampoline
global GetThreadStartTrampoline

extern ThreadEntryPointRunner

; -----------------------------------------------------------------------------
; Cooperative Context Switch with Extended AVX/FPU State Preservation
; void ContextSwitch(ThreadControlBlock* prev, ThreadControlBlock* next)
; rcx = prev (pointer to outgoing ThreadControlBlock)
; rdx = next (pointer to incoming ThreadControlBlock)
; -----------------------------------------------------------------------------
FpuStateOffset equ 256
CurrentRspOffset equ 24
IsExecutingOffset equ 212

ContextSwitch:
    ; 1. Push callee-saved registers (Microsoft x64 ABI)
    push rbx
    push rbp
    push rdi
    push rsi
    push r12
    push r13
    push r14
    push r15

    ; 2. Preserve incoming arguments in scratch registers r8 and r9 prior to clearing edx
    mov r8, rcx             ; r8 = prev
    mov r9, rdx             ; r9 = next
    mov rdi, r8             ; rdi = prev
    mov rsi, r9             ; rsi = next

    ; 3. Save AVX/YMM/FPU state if prev != null
    test rdi, rdi
    jz .skip_fpu_save
    mov eax, 7              ; component mask: x87 (1) | SSE (2) | AVX (4)
    xor edx, edx            ; clear edx (r8/r9 preserved!)
    xsave64 [rdi + FpuStateOffset]

    ; Save current RSP to prev->CurrentRsp
    mov [rdi + CurrentRspOffset], rsp

    ; SMP: Flush prev->CurrentRsp and release execution guard
    mfence
    mov dword [rdi + IsExecutingOffset], 0

.skip_fpu_save:
    ; 4. Load new RSP from next->CurrentRsp
    mov rsp, [rsi + CurrentRspOffset]

    ; 5. Restore AVX/YMM/FPU state for next thread
    mov eax, 7              ; component mask: x87 (1) | SSE (2) | AVX (4)
    xor edx, edx            ; clear edx
    xrstor64 [rsi + FpuStateOffset]

    ; 6. Pop callee-saved registers for the NEW thread
    pop r15
    pop r14
    pop r13
    pop r12
    pop rsi
    pop rdi
    pop rbp
    pop rbx

    ; 7. Resume execution at the target thread's return address
    ret

; -----------------------------------------------------------------------------
; Thread Start Trampoline
; Entry point for newly synthesized thread stacks.
; Jumped into via 'ret' at the end of ContextSwitch.
; -----------------------------------------------------------------------------
ThreadStartTrampoline:
    ; Ensure 16-byte stack alignment
    and rsp, -16

    ; Allocate 32-byte shadow space for Microsoft x64 calling convention
    sub rsp, 32

    ; Call C# thread entry point dispatcher
    call ThreadEntryPointRunner

    ; Clean up shadow space (in case runner returns)
    add rsp, 32

.hang:
    cli
    hlt
    jmp .hang

; -----------------------------------------------------------------------------
; ulong GetThreadStartTrampoline()
; Returns the address of ThreadStartTrampoline for initial synthetic stack setup
; -----------------------------------------------------------------------------
GetThreadStartTrampoline:
    lea rax, [rel ThreadStartTrampoline]
    ret

bits 64
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
IsExecutingOffset equ 240

ContextSwitch:
    test rcx, rcx
    jz .restore_next

    ; 1. Preserve Win64 callee-saved registers on outgoing stack
    push rbx
    push rbp
    push rdi
    push rsi
    push r12
    push r13
    push r14
    push r15

    ; 2. Preserve incoming arguments in scratch registers
    mov r8, rdx             ; r8 = next
    mov r9, rcx             ; r9 = prev

    ; 3. Save FPU/SSE/AVX state into prev->FpuState (offset 256)
    mov eax, 7              ; XFEATURE_MASK_X87 | XFEATURE_MASK_SSE | XFEATURE_MASK_AVX
    xor edx, edx
    xsave64 [r9 + FpuStateOffset]

    ; 4. Save outgoing RSP
    mov [r9 + CurrentRspOffset], rsp

    ; 5. Switch to incoming thread stack
    mov rsp, [r8 + CurrentRspOffset]

    ; 6. Mark outgoing thread as no longer executing on new stack
    mov dword [r9 + IsExecutingOffset], 0
    mfence

    mov rdx, r8
    jmp .do_restore

.restore_next:
    mov rsp, [rdx + CurrentRspOffset]

.do_restore:
    ; 7. Restore FPU/SSE/AVX state from next->FpuState (offset 256)
    mov r8, rdx
    mov eax, 7
    xor edx, edx
    xrstor64 [r8 + FpuStateOffset]

    ; 8. Restore callee-saved registers
    pop r15
    pop r14
    pop r13
    pop r12
    pop rsi
    pop rdi
    pop rbp
    pop rbx
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

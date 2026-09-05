default rel
section .text

global ContextSwitch
global ThreadStartTrampoline
global GetThreadStartTrampoline

extern ThreadEntryPointRunner

; -----------------------------------------------------------------------------
; Cooperative Context Switch
; void ContextSwitch(ulong* oldRspOut, ulong newRsp)
; rcx = oldRspOut (pointer to location where current RSP is saved)
; rdx = newRsp    (new stack pointer to restore)
; -----------------------------------------------------------------------------
ContextSwitch:
    ; 1. Push callee-saved registers
    push rbx
    push rbp
    push r12
    push r13
    push r14
    push r15

    ; 2. Save current RSP to [rcx]
    mov [rcx], rsp

    ; 3. Load new RSP from rdx
    mov rsp, rdx

    ; 4. Pop callee-saved registers in reverse order
    pop r15
    pop r14
    pop r13
    pop r12
    pop rbp
    pop rbx

    ; 5. Resume execution at the target thread's return address
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

default rel
section .text

global SupervisorEntry
extern SupervisorMain

global Syscall

; -----------------------------------------------------------------------------
; Supervisor Flat Binary Entry Point
; -----------------------------------------------------------------------------
SupervisorEntry:
    and rsp, -16
    sub rsp, 32
    call SupervisorMain
    add rsp, 32

.halt:
    hlt
    jmp .halt

; -----------------------------------------------------------------------------
; Hardware Syscall Wrapper
; ulong Syscall(ulong num, ulong a1, ulong a2, ulong a3, ulong a4, ulong a5, ulong a6)
; -----------------------------------------------------------------------------
Syscall:
    push rbx
    push rbp
    push r12
    push r13
    push r14
    push r15

    mov rax, rcx            ; num
    mov rdi, rdx            ; a1
    mov rsi, r8             ; a2
    mov rdx, r9             ; a3

    ; 6 pushes (48 bytes) + return address (8 bytes) + 32-byte shadow space = 88 bytes
    mov r10, [rsp + 88]     ; a4
    mov r12, [rsp + 96]     ; a5
    mov r13, [rsp + 104]    ; a6

    syscall

    pop r15
    pop r14
    pop r13
    pop r12
    pop rbp
    pop rbx
    ret

; -----------------------------------------------------------------------------
; Freestanding RyuJIT Runtime Stubs
; -----------------------------------------------------------------------------
global RhpReversePInvoke
global RhpReversePInvokeReturn
global RhpPInvoke
global RhpPInvokeReturn
global RhpFallbackFailFast
global RhpThrowEx
global __security_check_cookie
global RhpAssignRef
global RhpNewFast
global RhpInitialDynamicInterfaceDispatch

RhpReversePInvoke: ret
RhpReversePInvokeReturn: ret
RhpPInvoke: ret
RhpPInvokeReturn: ret
RhpFallbackFailFast: ret
RhpThrowEx: ret
__security_check_cookie: ret

RhpAssignRef:
    mov [rcx], rdx
    ret

RhpNewFast:
    lea rax, [rel .static_obj_buf]
    ret
.static_obj_buf: times 256 db 0

RhpInitialDynamicInterfaceDispatch:
    ret

section .data
global __security_cookie
__security_cookie:
    dq 0x00002B992DDFA232

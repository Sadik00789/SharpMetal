default rel
section .text

global InputHidEntry
extern InputHidMain

global Syscall
global PortIn8
global PortOut8

; -----------------------------------------------------------------------------
; InputHid Flat Binary Entry Point
; -----------------------------------------------------------------------------
InputHidEntry:
    and rsp, -16
    sub rsp, 32
    call InputHidMain
    add rsp, 32

.halt:
    hlt
    jmp .halt

; -----------------------------------------------------------------------------
; Hardware Syscall Wrapper
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
; Direct Port I/O Routines (Requires IOPL = 3)
; -----------------------------------------------------------------------------
PortIn8:
    mov dx, cx
    in al, dx
    movzx eax, al
    ret

PortOut8:
    mov dx, cx
    mov al, dl
    out dx, al
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

default rel
section .text

global ShellEntry
extern ShellMain

global Syscall
global GetFontGlyphs

; -----------------------------------------------------------------------------
; Shell Flat Binary Entry Point
; -----------------------------------------------------------------------------
ShellEntry:
    and rsp, -16
    sub rsp, 32
    call ShellMain
    add rsp, 32

.halt:
    mov rcx, 1
    call Syscall
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
; Monospace Bitmap Font Data Accessor
; -----------------------------------------------------------------------------
GetFontGlyphs:
    lea rax, [rel .font_data]
    ret

section .rdata
.font_data:
    incbin "src/libs/Microkernel.Drawing/Resources/font.bin"

section .text
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

global RhpStelemRef
global RhpNewArray
global RhpNewArrayFast

RhpStelemRef:
    mov [rcx], rdx
    ret

RhpNewFast:
    ; Allocate 256-byte zeroed object slot in RW .bss (never write into .text)
    push rcx
    push rdi
    lea rcx, [rel obj_bump_ptr]
    mov rax, [rcx]
    test rax, rax
    jnz .obj_check
    lea rax, [rel obj_heap_buf]
.obj_check:
    lea rdx, [rax + 256]
    lea rdi, [rel obj_heap_buf + 65536]
    cmp rdx, rdi
    jbe .obj_zero
    lea rax, [rel obj_heap_buf]
    lea rdx, [rax + 256]
.obj_zero:
    mov [rcx], rdx
    mov rdi, rax
    xor rcx, rcx
.obj_zero_loop:
    mov qword [rdi + rcx*8], 0
    inc rcx
    cmp rcx, 32
    jb .obj_zero_loop
    pop rdi
    pop rcx
    ret

RhpNewArrayFast:
RhpNewArray:
    ; rcx = MethodTable*
    ; rdx = length
    push rbx
    push r12
    push r13
    push rdi
    mov rbx, rcx        ; MethodTable
    mov r12, rdx        ; length

    ; Reject negative or oversized element counts that exceed array_heap_buf
    test rdx, rdx
    js .arr_oom
    cmp rdx, 65500
    ja .arr_oom

    ; calculate size: 16 (header) + length * 8 + 15, aligned to 16
    mov rax, rdx
    shl rax, 3
    add rax, 31
    and rax, -16
    mov r13, rax        ; allocation size

    ; Bump pointer allocation with bounds check
    lea rcx, [rel array_bump_ptr]
    mov rax, [rcx]
    test rax, rax
    jnz .allocated
    lea rax, [rel array_heap_buf]
.allocated:
    lea rdx, [rax + r13]
    lea rdi, [rel array_heap_buf + 524288]
    cmp rdx, rdi
    ja .arr_oom
    mov [rcx], rdx

    ; Zero out the allocated memory
    mov rdi, rax
    mov rcx, r13
    shr rcx, 3
    xor edx, edx
.zero_loop:
    test rcx, rcx
    jz .zero_done
    mov [rdi], rdx
    add rdi, 8
    dec rcx
    jmp .zero_loop
.zero_done:

    ; Set MethodTable and Length
    mov [rax], rbx
    mov [rax + 8], r12d

    pop rdi
    pop r13
    pop r12
    pop rbx
    ret

.arr_oom:
    xor eax, eax
    pop rdi
    pop r13
    pop r12
    pop rbx
    ret

RhpInitialDynamicInterfaceDispatch:
    ret

section .bss
align 16
obj_bump_ptr: resq 1
obj_heap_buf: resb 65536
array_bump_ptr: resq 1
array_heap_buf: resb 524288

section .data

section .data
global __security_cookie
__security_cookie:
    dq 0x00002B992DDFA232

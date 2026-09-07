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

    ; 3. Push iretq frame (5 qwords):
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

    ; 4. Execute iretq to drop CPL from 0 to 3
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

    ; 1. Load User Data segment selector into segment registers
    mov ax, 0x1B
    mov ds, ax
    mov es, ax
    mov fs, ax
    ; Do not write to gs: writing to gs resets IA32_GS_BASE to 0!

    ; 2. Stack currently holds 5 iretq qwords (RIP, CS, RFLAGS, RSP, SS)
    ; Pop iretq frame and drop CPL from 0 to 3
    iretq

; -----------------------------------------------------------------------------
; ulong GetUserThreadTrampoline()
; Returns the address of UserThreadTrampoline for synthetic user stack setup
; -----------------------------------------------------------------------------
GetUserThreadTrampoline:
    lea rax, [rel UserThreadTrampoline]
    ret

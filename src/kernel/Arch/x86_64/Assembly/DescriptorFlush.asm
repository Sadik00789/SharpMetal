default rel
section .text

; -----------------------------------------------------------------------------
; GDT, TSS, Segment, and IDT Flush Routines (Microsoft x64 Calling Convention)
; -----------------------------------------------------------------------------

global LoadGdt
global ReloadSegments
global LoadTss
global LoadIdt

; void LoadGdt(GdtPointer* ptr) - rcx = ptr
LoadGdt:
    lgdt [rcx]
    ret

; void ReloadSegments(ushort codeSeg, ushort dataSeg) - rcx = codeSeg (0x08), rdx = dataSeg (0x10)
global ReloadSegments
ReloadSegments:
    mov ds, dx
    mov es, dx
    mov ss, dx
    mov fs, dx
    mov gs, dx

    push rcx                  ; Push 64-bit code selector (0x08)
    lea rax, [rel .reload_cs] ; Push return address
    push rax
    retfq                     ; 64-bit far return to reload CS
.reload_cs:
    ret

; void LoadTss(ushort tssSelector) - rcx = tssSelector (0x28)
LoadTss:
    ltr cx
    ret

; void LoadIdt(IdtPointer* ptr) - rcx = ptr
LoadIdt:
    lidt [rcx]
    ret

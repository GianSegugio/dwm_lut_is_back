; OverlaysEnabledThunk.asm  (x64 MASM)
;
; Register-preserving detour for COverlayContext::OverlaysEnabled on 25H2.
;
; Why this exists: the real OverlaysEnabled preserves every register except al, so DWM's compiler relies
; on volatile registers surviving the call (interprocedural register allocation). In particular
; COverlayContext::OverlayPlaneInfo::IsDFlipOnMPO dereferences r8 after the call; a plain C++ detour
; clobbers it and faults (the fullscreen crash). This thunk saves/restores every volatile the real callee
; preserves -- rcx, rdx, r8, r9, r10, r11 -- around the C++ hook, so all callers behave correctly.
;
; (History: a minimal variant that preserved only r8 and let edx stay clobbered was tried to reproduce
; v1.0.1's incidental IndependentFlip suppression for fullscreen games. It proved unreliable -- the
; leftover edx depends on runtime state, so it suppressed flip only intermittently and made the LUT
; flicker with DWM's composite-vs-flip decision (e.g. tracking the mouse cursor over fullscreen video).
; Fullscreen-game LUT is a DWM IndependentFlip limitation, not something this hook can reliably control,
; so we keep the correct, consistent full-preserving thunk.)
;
; Stack: entry rsp%16==8; 6 pushes -> rsp%16==8; sub 28h -> rsp%16==0 (32-byte shadow); call sees %16==8.

EXTERN COverlayContext_OverlaysEnabled_hook : PROC   ; bool (__fastcall)(void* self) -- self in rcx

.CODE

OverlaysEnabled_thunk PROC
    push    rcx                                    ; self (real OverlaysEnabled reads but preserves rcx)
    push    rdx
    push    r8
    push    r9
    push    r10
    push    r11
    sub     rsp, 28h                               ; shadow space (+ keep rsp 16-aligned across the call)
    call    COverlayContext_OverlaysEnabled_hook   ; rcx still = self; returns bool in al
    add     rsp, 28h
    pop     r11
    pop     r10
    pop     r9
    pop     r8
    pop     rdx
    pop     rcx
    ret
OverlaysEnabled_thunk ENDP

END

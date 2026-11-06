; Aspecto del instalador: tema claro u oscuro según Windows y fondo fijo con los colores de la
; marca. Sin animación: a 7 fps daba saltos. Las imágenes salen de
; art\generate-art.ps1 (requiere Inno Setup 6.7+). Lo incluye setup.iss.

; ISCC /DWizardMode=light|dark fuerza un tema (para revisar el aspecto); por defecto, dynamic.
#ifndef WizardMode
  #define WizardMode "dynamic"
#endif

[Setup]
; dynamic: sigue el modo claro/oscuro de Windows. El fondo propio activa el estilo windows11.
WizardStyle=modern {#WizardMode}
; La bienvenida (Inno la oculta por defecto) enseña el logo sobre el fondo.
DisableWelcomePage=no
#if WizardMode == "dark"
WizardBackImageFile=art\back-dark-*.png
#else
WizardBackImageFile=art\back-light-*.png
#endif
WizardBackImageFileDynamicDark=art\back-dark-*.png
; Color del resto de ventanas (idioma, desinstalación, avisos).
WizardBackColor=#f7f5ff
WizardBackColorDynamicDark=#0f0f1a
WizardImageFile=art\hero-*.png
WizardSmallImageFile=art\logo-*.png
SetupIconFile=..\..\app.ico

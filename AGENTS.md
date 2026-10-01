# Instrucciones para agentes en este repositorio

Este repositorio contiene la fuente destinada al portafolio público de DWG Translator. No contiene una instalación activa, credenciales, dibujos ni binarios de Autodesk. El titular indicado es Ricardo Reyes; la publicación del código no concede permisos generales de reutilización.

## Al recibir sólo la ruta del repositorio

1. Verifica el directorio de trabajo y lee `README.md`, `BUILDING.md`, `INSTALLATION.md` y `docs/ADR/README.md`. Inspecciona `git status` y cualquier `AGENTS.md` anidado antes de cambiar archivos. Conserva los cambios existentes.
2. Distingue entre **fuente compilable**, **paquete portable preparado**, **interfaces que arrancan** y **flujo CAD validado**. Reporta la evidencia de cada nivel; no infieras uno del anterior.
3. Usa `scripts/Build-Portable.ps1` para reconstruir. El paquete se crea bajo `artifacts/` y el script no reemplaza una salida existente. La guía `INSTALLATION.md` fija el orden de preparación, configuración y verificaciones.
4. Mantén configuración activa, secretos, logs, corpus y salidas fuera de Git. Nunca copies al repositorio DLL de Autodesk, claves, datos de autorización, DWG de terceros ni rutas de una instalación anterior.

## Acciones que requieren autorización específica

- Instalar herramientas o runtimes, copiar el paquete a una ubicación de operación, cambiar ACL, Credential Manager, configuración del cliente MCP o cualquier ajuste de Windows.
- Iniciar o automatizar AutoCAD, abrir un DWG o crear una salida CAD. Primero identifica un sample seguro autorizado, completa preflight y solicita aprobación de la acción concreta. Nunca sobrescribas el origen; usa salida nueva, validación y revisión visual humana.
- Hacer llamadas reales al proveedor de traducción o publicar, versionar, subir o crear un repositorio remoto. Una solicitud de reconstrucción o diagnóstico no autoriza estas acciones.

## Criterios de calidad

- AutoCAD es el único motor DWG. Los adaptadores CAD no deben conocer credenciales ni llamadas al proveedor de traducción.
- El alcance de escritura es `TEXT`/`MTEXT` directo; fields son sólo lectura/excluidos, y atributos, tablas y Xrefs quedan fuera de este flujo.
- Al cambiar código, actualiza las pruebas pertinentes. Al cambiar alcance, compatibilidad, contratos, seguridad o recuperación, actualiza documentación y ADR.
- Informa archivos cambiados, comandos y resultados de pruebas, acciones no verificadas, riesgos y `git status`. No ocultes fallos ni presentes un build como validación funcional CAD.

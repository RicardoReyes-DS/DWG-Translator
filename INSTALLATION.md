# Instalación desde una copia nueva del repositorio

Esta guía permite a un agente preparar un **paquete portable** y configurar sus interfaces en una máquina Windows nueva. No hay MSI ni configuración activa en Git. El agente debe leer primero [AGENTS.md](AGENTS.md) y la [receta de compilación](BUILDING.md). Las rutas entre `<...>` son valores que el operador de la nueva máquina debe elegir; no representan una instalación previa.

Para iniciar el trabajo en Codex, indica la ruta del clon **y el objetivo**: «Trabaja en `<RUTA_DEL_CLON>`, sigue `AGENTS.md` e `INSTALLATION.md`, reconstruye desde fuente y presenta evidencia de cada nivel. Antes de instalar dependencias, cambiar Windows, configurar credenciales o abrir DWG, solicita la autorización específica correspondiente». La ruta por sí sola no aporta AutoCAD, credenciales ni permiso para modificar la máquina.

## 1. Inventario antes de modificar la máquina

1. Confirma que el checkout es el repositorio esperado, que `git status` no muestra cambios que debas preservar y que están presentes `src/`, `tools/`, `bundle/`, `config/`, los archivos `packages.lock.json` de los proyectos y `global.json`.
2. Comprueba Windows x64, PowerShell, `dotnet --list-sdks` y `dotnet --list-runtimes`. La compilación selecciona SDK 10; la GUI publicada requiere .NET 8 Desktop Runtime, Host y MCP requieren .NET 8 ASP.NET Core Runtime, y los adaptadores CAD usan .NET 10 dentro del entorno compatible de AutoCAD.
3. Para el paquete completo, identifica en una instalación de AutoCAD 2026 obtenida por separado el directorio que contiene `AcCoreMgd.dll`, `AcDbMgd.dll` y `AcMgd.dll`. Verifica que existe; no copies esas DLL al repositorio ni al paquete.
4. Define carpetas privadas nuevas para runtime, datos, logs y salidas. Confirma espacio libre, permisos, una URL HTTP de loopback libre para Host y que no se reemplazará otra instalación.

Si faltan SDK, runtimes o AutoCAD, registra el bloqueo. Instalar dependencias o alterar Windows requiere la autorización específica indicada en `AGENTS.md`.

## 2. Compilar desde la fuente

Desde la raíz del clon:

```powershell
./scripts/Build-Portable.ps1 -CoreOnly -OutputDirectory ./artifacts/core-check
```

Esta primera pasada prueba 361 casos y publica GUI, Host, CLI y MCP sin CAD. Para el paquete completo, usa **un directorio de salida nuevo**:

```powershell
./scripts/Build-Portable.ps1 -AutoCADInstallDir '<DIRECTORIO_DE_REFERENCIAS_CAD>' -OutputDirectory ./artifacts/portable
```

El script vuelve a ejecutar las pruebas, compila los dos adaptadores, construye bundles separados y genera `SHA256SUMS.txt`. Comprueba que terminaron las pruebas, que ambos proyectos CAD compilaron, que existen los cuatro `.exe` y los dos `PackageContents.xml`, y que los hashes corresponden a los archivos generados. El paquete no debe contener `AcCoreMgd.dll`, `AcDbMgd.dll` ni `AcMgd.dll`. Si algo falla, conserva el diagnóstico y usa otra carpeta de salida al reintentar.

## 3. Preparar un runtime privado

Con autorización para cambiar la máquina, copia **todo** `artifacts/portable/` a un directorio privado nuevo `<RUNTIME_PRIVADO>`. Conserva la estructura `GUI/`, `Host/`, `CLI/`, `MCP/` y `Bundles/`; no mezcles versiones ni sustituyas una carpeta activa. Verifica los hashes tras la copia y conserva la ruta exacta del paquete usado. Los bundles deben ser directorios ordinarios, sin enlaces o junctions, con sus DLL dentro de `Contents/Windows/`. No hace falta registrarlos para carga automática de AutoCAD: los manifiestos declaran `LoadOnAutoCADStartup="False"` y el programa hace carga manual controlada.

Mantén `<RUNTIME_PRIVADO>` fuera del clon y deja configuración, workspace, logs y dibujos en directorios distintos. La copia de `-CoreOnly` sirve para pruebas de código, pero **no** es una instalación capaz de operar con DWG.

## 4. Configurar la GUI

Con autorización para crear configuración local, copia `config/gui-bootstrap.example.json` a `%LOCALAPPDATA%\DwgTranslator\bootstrap.v1.json`. Rellena rutas absolutas del nuevo entorno:

| Campo | Valor requerido |
| --- | --- |
| `allowedDataRoot` | Directorio privado existente de datos; nunca la raíz de una unidad. |
| `workspaceRoot` | Subdirectorio del anterior, sin enlaces de redirección. |
| `autoCadExecutablePath` | Ejecutable AutoCAD local compatible. |
| `readOnlyBundleDirectory` / `writeBundleDirectory` | Directorios separados bajo `<RUNTIME_PRIVADO>\Bundles`. |
| `openAiModel` | Modelo disponible para el nuevo operador. |
| `openAiCredentialReference` | Referencia de Windows Credential Manager, sin valor secreto en JSON. |

El archivo debe pertenecer al usuario que ejecuta la GUI. Revisa su ACL: solamente ese usuario, `SYSTEM` o Administradores pueden tener permiso de escritura. El programa rechaza un bootstrap con otros escritores. Tras sustituir **todos** los valores `<...>`, cambia `enabled` a `true`. La GUI usa esta ruta fija; no toma la configuración del Host. La credencial de traducción se incorpora localmente mediante el flujo de configuración de la GUI, nunca en el repositorio ni en comandos.

Abre la GUI y comprueba que muestra encabezado, campos y botones, sin iniciar un trabajo. Si aparece modo configuración, revisa los códigos `BOOTSTRAP_*` y la validación en `ProductionBootstrap.cs` y `ProductionComposition.cs`. Ver una ventana **no demuestra** funcionamiento CAD.

## 5. Configurar Host, CLI y MCP

Con autorización para crear configuración y credencial local, copia `config/agent-host.example.json` a `<CONFIG_PRIVADA_AGENT>.json`, fuera de Git. Para la primera prueba sólo reemplaza `workspaceRoot`, `logRoot` y `hostExecutablePath` por rutas absolutas del nuevo entorno; workspace y logs deben ser distintos. Elige una `hostUrl` HTTP de loopback libre y una `credentialReference` propia. Pon `enabled: true` y deja `executionEnabled`, `openAiEnabled` y `batchExecutionEnabled` en `false`. Los campos CAD vacíos del ejemplo son deliberados.

Inicia Host en una terminal y mantenlo abierto. El primer arranque crea su token de Host en Windows Credential Manager si todavía no existe; registra la referencia, **nunca el token**. Desde otra terminal ejecuta:

```powershell
& '<RUNTIME_PRIVADO>\Host\DwgTranslator.AgentHost.exe' --config '<CONFIG_PRIVADA_AGENT>.json'
& '<RUNTIME_PRIVADO>\CLI\DwgTranslator.AgentCli.exe' health --config '<CONFIG_PRIVADA_AGENT>.json'
```

La segunda línea se ejecuta en otra terminal mientras Host sigue activo. Para MCP, configura en el cliente un servidor **stdio** cuyo comando sea `<RUNTIME_PRIVADO>\MCP\DwgTranslator.AgentMcpServer.exe` y cuyos argumentos sean `--config` y la ruta absoluta del **mismo** JSON. Comprueba `health` y `capabilities` mediante el cliente MCP. MCP exige un Host saludable y no lo inicia automáticamente. La ubicación y sintaxis de registro del cliente MCP dependen del cliente actual; inspecciónalas antes de modificar su configuración.

La configuración del Host permite sólo operaciones de lectura por defecto. Para inspección CAD controlada, completa `allowedDwgRoot`, `autoCadExecutablePath`, `readOnlyBundleDirectory` y `readOnlyAdapterAssemblyPath`, comprueba que existen, y sólo después habilita `executionEnabled`. Para traducción y escritura, configura también `outputDwgRoot`, el bundle/DLL de escritura, modelo y modelos accesibles, la credencial del proveedor y una lista explícita de operaciones permitidas antes de habilitar `openAiEnabled`. Cuando se añade `allowedOperations`, esa lista **sustituye** al conjunto de lectura predeterminado: debe incluir cada operación de consulta y de escritura que se quiera usar. Los identificadores exactos están en `AgentHostOperations` dentro de `src/DwgTranslator.Agent/AgentHostSecurity.cs`. Para lotes, añade `batchOutputRoots` absolutos y exactos antes de habilitar `batchExecutionEnabled`. Reinicia Host tras cada cambio de configuración. Las reglas de validación están en `AgentConfigurationLoader.cs`.

## 6. Criterios para declarar el resultado

| Nivel | Evidencia mínima |
| --- | --- |
| Fuente reconstruida | Pruebas y compilación completadas; paquete con estructura y hashes válidos. |
| Interfaces listas | GUI visible, Host/CLI health y herramientas MCP de salud/capacidades responden con CAD y proveedor deshabilitados. |
| CAD validado | Sample seguro aprobado, preflight, ejecución CAD autorizada por separado, origen intacto, salida nueva, readback/validación y revisión visual humana. |
| Traducción operativa | Además de CAD validado, credencial y modelo configurados con autorización, traducción y revisión aprobadas, escritura a salida distinta y verificación final. |

No declares los dos últimos niveles a partir de un build o una comprobación de salud. Conserva evidencia técnica sin texto de planos ni secretos. Si una etapa falla, detén la siguiente y comunica el error exacto. Para revertir una preparación local, identifica primero los procesos y archivos creados por **esa** instalación; detener servicios, restaurar configuración anterior, borrar credenciales o eliminar carpetas requiere la autorización pertinente.

# Compilar y generar el instalador

## Requisitos

| Para | Necesitas |
|---|---|
| Compilar y generar el instalador | [SDK de .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) (con `dotnet --list-sdks` debe aparecer una versión 10.x) |
| Trabajar desde Visual Studio (opcional) | Visual Studio con la carga de trabajo «Desarrollo de escritorio de .NET» |
| Ver el proyecto del instalador dentro de Visual Studio (opcional) | Extensión **HeatWave** de FireGiant (Extensiones → Administrar extensiones) |

No hace falta instalar WiX: el proyecto del instalador lo descarga solo de NuGet la primera vez.
El PC donde se instale la app **no necesita .NET**: va incluido en el instalador.

## Estructura

```
WidgetWindowDemo.sln
├─ WidgetWindowDemo\                    la app (WPF, .NET 10)
├─ Installer\                           el instalador MSI (WiX Toolset 5)
│   ├─ WidgetManager.Installer.wixproj  publica la app y genera el MSI
│   └─ Package.wxs                      qué instala y cómo
├─ build-installer.ps1                  atajo: MSI + versión portable ZIP
└─ artifacts\                           resultados (no se suben a Git)
```

## 1. Compilar y ejecutar la app (desarrollo)

```powershell
dotnet build WidgetWindowDemo.sln
dotnet run --project WidgetWindowDemo\WidgetWindowDemo.csproj
```

En Visual Studio: configuración **Debug** → F5. En Debug solo se compila la app (rápido); el instalador no.

> Si falla con «el archivo está siendo utilizado por otro proceso», la app sigue abierta en la bandeja:
> clic derecho en su icono → **Salir**, y vuelve a compilar.

## 2. Generar el instalador

Elige una de estas tres formas; todas producen lo mismo en `artifacts\`.

**a) Script (recomendado: también crea la versión portable)**

```powershell
.\build-installer.ps1                      # x64
.\build-installer.ps1 -Platforms x64,arm64 # también para equipos ARM
```

> Si `.\build-installer.ps1` falla con «la ejecución de scripts está deshabilitada en este sistema», usa
> **`.\build-installer.cmd`** (mismos parámetros, o doble clic en el Explorador): lanza el script sin depender de esa
> restricción. Alternativa permanente: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`.

**b) Un solo comando**

```powershell
dotnet build Installer\WidgetManager.Installer.wixproj -c Release
dotnet build Installer\WidgetManager.Installer.wixproj -c Release -p:InstallerPlatform=arm64 -p:Platform=arm64
```

**c) Visual Studio**

Configuración **Release** → Compilar → Compilar solución. En Release se compila la app y se genera el MSI.

### Resultados

| Archivo | Para qué |
|---|---|
| `artifacts\WidgetManager-<versión>-x64.msi` | Instalador (doble clic para instalar) |
| `artifacts\WidgetManager-<versión>-x64.zip` | Versión portable: descomprimir y abrir `WidgetWindowDemo.exe` (solo con el script) |
| `artifacts\publish\win-x64\` | Los archivos publicados que van dentro del instalador |

## 3. Publicar una versión nueva

1. Sube `<Version>` en `WidgetWindowDemo\WidgetWindowDemo.csproj` (por ejemplo `1.1.0` → `1.2.0`).
   **Es obligatorio**: con la misma versión el instalador no actualiza la instalación anterior.
2. Genera el instalador (paso 2).
3. Al ejecutar el MSI nuevo, sustituye a la versión instalada y conserva los widgets del usuario.

No cambies nunca el `UpgradeCode` de `Installer\Package.wxs`: es lo que identifica el producto entre versiones.

## Qué hace el instalador

- Instala para todos los usuarios en `C:\Program Files\Widget Manager` (se puede cambiar en el asistente).
- Crea accesos directos en el menú Inicio y en el escritorio.
- Si la app está abierta, le pide que se cierre guardando sus widgets antes de actualizar.
- Al terminar ofrece «Abrir Widget Manager».
- Se desinstala desde Configuración → Aplicaciones → Aplicaciones instaladas.
- Requiere Windows 10 u 11.

## Datos del usuario

La app guarda sus datos en `%LOCALAPPDATA%\WidgetWindowDemo` (no en la carpeta del programa):

| Ruta | Contenido |
|---|---|
| `widgets.json` | lista de widgets y su configuración |
| `Assets\Widgets\` | copias de las imágenes y vídeos importados |
| `logs\` | registros de la app (también: botón «Registros de la app» o menú de la bandeja) |

Desinstalar no borra esta carpeta.

## Problemas conocidos

- **SmartScreen avisa de «editor desconocido»**: el MSI no está firmado. Se evita firmándolo con un certificado de
  firma de código (`signtool sign ...`) después de generarlo.
- **Vídeos que no se reproducen en Windows «N»**: esas ediciones no traen códecs de vídeo; instala el
  «Media Feature Pack» de Microsoft.

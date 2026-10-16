# WidgetWindows Blake

Aplicación de escritorio para Windows 10/11 que permite colocar widgets (imágenes, vídeos y más) flotando sobre el escritorio.

## Descargar e instalar

1. Ve a la sección **[Releases](../../releases/latest)** de este repositorio.
2. Descarga el archivo que corresponda a tu equipo:
   - `WidgetManager-<versión>-x64.msi` → la mayoría de PCs (Intel/AMD).
   - `WidgetManager-<versión>-arm64.msi` → equipos ARM (si está disponible).
   - `WidgetManager-<versión>-x64.zip` → versión portable, sin instalar: descomprime y abre `WidgetWindowDemo.exe`.
3. Haz doble clic en el `.msi` y sigue el asistente.

No necesitas instalar .NET: ya va incluido.

> **Aviso de SmartScreen («Windows protegió su PC»)**: aparece porque el instalador no está firmado.
> Pulsa **Más información → Ejecutar de todas formas**.

### Qué hace el instalador

- Instala en `C:\Program Files\Widget Manager` y crea accesos directos en el menú Inicio y el escritorio.
- Para actualizar, basta con instalar la versión nueva: conserva tus widgets.
- Se desinstala desde **Configuración → Aplicaciones → Aplicaciones instaladas**.

Tus datos se guardan en `%LOCALAPPDATA%\WidgetWindowDemo` (no se borran al desinstalar).

### Problemas conocidos

- **Los vídeos no se reproducen en Windows «N»**: instala el *Media Feature Pack* de Microsoft.

## Compilar desde el código

Requiere el [SDK de .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/fabricioqm/WidgetWindows-Blake.git
cd WidgetWindows-Blake
dotnet run --project WidgetWindowDemo\WidgetWindowDemo.csproj   # ejecutar
.\build-installer.ps1                                           # generar MSI + ZIP en artifacts\
```

Guía completa: [COMPILAR.md](COMPILAR.md).

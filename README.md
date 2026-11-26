# TabLink

Usa una tablet o un teléfono Android como segunda pantalla de Windows mediante USB. Incluye una app Android nativa, un compañero de Windows y control táctil.

[Descargar](https://github.com/feynnmark/TabLink/releases/latest) · [Compatibilidad](docs/COMPATIBILITY.md) · [Compilar](docs/BUILDING.md)

## Requisitos

| Componente | Requisito |
| --- | --- |
| PC | Windows 11 de 64 bits, .NET Framework 4.8 y GPU NVIDIA con codificación HEVC NVENC |
| Android | Android 8.0 o posterior, depuración USB y decodificador HEVC de hardware |
| Conexión | Cable USB de datos; no basta un cable de carga |
| Primera instalación | Internet para descargar dependencias y permisos de administrador para el controlador de pantalla |

La conexión puede utilizar un puerto USB de la placa base, del portátil o de una tarjeta gráfica. No requiere un puerto de vídeo USB-C especial, Wi-Fi, root ni una cuenta.

## Descargar e instalar

1. Abre [Releases](https://github.com/feynnmark/TabLink/releases/latest) y descarga **TabLink-0.6.1-Windows.zip**. El archivo **Source code** contiene el código para desarrolladores.
2. Extrae el ZIP en una carpeta local. No ejecutes el instalador desde el ZIP.
3. Ejecuta **Install.cmd**. Instala TabLink para tu usuario y descarga ADB, FFmpeg y el controlador VDD desde sus fuentes oficiales, con verificación SHA-256. La primera instalación descarga aproximadamente 200 MB.
4. En la ventana de TabLink, pulsa **Preparar pantalla virtual (una vez)**. Acepta el aviso de administrador. El controlador aparece como **MikeTheTech / SignPath Foundation**; puedes autorizar esa instalación sin marcar **Siempre confiar**.
5. En Android, activa **Opciones de desarrollador** pulsando siete veces **Número de compilación**, dentro de los ajustes de información del dispositivo. Activa **Depuración USB**.
6. Conecta Android al PC, desbloquéalo y acepta **Permitir depuración USB**. Marca **Permitir siempre desde este ordenador** si es un PC de confianza. Si el fabricante lo requiere, habilita también la instalación por USB.
7. Pulsa **Conectar tablet**. El compañero instala el APK y abre la pantalla automáticamente. También puedes instalar **TabLink.apk** de la misma release de forma manual.

En Windows, abre **Configuración > Sistema > Pantalla** para ordenar los monitores y ajustar la escala. TabLink conserva la distribución de los monitores físicos y sitúa inicialmente la pantalla nueva debajo del principal, evitando solapamientos. Guarda la posición de la pantalla virtual al desconectarla.

El instalador añade un acceso directo y el inicio automático de TabLink. Después de la primera configuración, basta conectar el dispositivo autorizado. Puedes desactivar el inicio automático desde la ventana del compañero. En algunos fabricantes, Android solicita una confirmación adicional de instalación o lanzamiento.

## Uso

- **Extender escritorio** crea una pantalla independiente. **Duplicar pantalla principal** transmite el monitor principal y no necesita instalar VDD.
- El menú de Android permite activar o desactivar el táctil, mostrar estadísticas, reconectar o cerrar la pantalla.
- La X de Windows deja TabLink en la bandeja. **Cerrar TabLink** termina el compañero.
- Al salir de Android o desconectar el cable, se retira la pantalla virtual. Las aplicaciones se recolocan según el comportamiento de Windows.

La app consulta la pantalla y el decodificador de Android para negociar una resolución compatible, 30, 60, 75, 90 o 120 Hz, y HEVC de 8 o 10 bits. Utiliza 10 bits cuando están disponibles. Esto reduce la cuantización del vídeo, pero no cambia la profundidad física del panel. Si no existe un modo nativo compatible, selecciona uno de menor resolución.

Los fps de las estadísticas corresponden a imágenes presentadas. Un escritorio estático puede producir pocas imágenes nuevas. La frecuencia seleccionada es un objetivo; USB, GPU, temperatura y carga del sistema pueden reducir el rendimiento.

## Si no conecta

| Síntoma | Qué revisar |
| --- | --- |
| No aparece ningún dispositivo | Prueba otro cable de datos o puerto, desbloquea Android e instala el controlador USB del fabricante si Windows lo necesita. |
| Conexión no autorizada | Acepta el aviso de depuración en Android. Si no aparece, revoca las autorizaciones de depuración y vuelve a conectar. |
| Android bloquea la instalación | Acepta la instalación local o habilita la opción de instalación por USB del fabricante. |
| Falta pantalla virtual | Ejecuta **Preparar pantalla virtual** y revisa el resultado. Puedes probar **Duplicar** mientras tanto. |
| Ya tienes VDD para otra app | TabLink conserva esa instalación y no la reconfigura. Usa **Duplicar** o retira primero el VDD anterior si deseas que TabLink lo gestione. |
| No hay un modo compatible | Repite la preparación del controlador para actualizar sus modos y vuelve a conectar. |
| No inicia el vídeo | Comprueba compatibilidad HEVC de hardware en Android y HEVC NVENC en el PC, y actualiza el controlador NVIDIA. |
| Táctil bloqueado en una app | Windows puede impedir la entrada hacia procesos elevados y el escritorio seguro. |

Conecta un solo dispositivo receptor a la vez. El compañero utiliza el primer Android USB autorizado; excluye emuladores y conexiones ADB de red.

Los registros se guardan localmente en `%LOCALAPPDATA%\TabLink`. `TabLink.exe --diagnose` genera información de monitores. Revisa y elimina rutas, identificadores o contenido privado antes de compartir diagnósticos.

## Privacidad y seguridad

La transmisión usa `127.0.0.1` y `adb reverse` a través de USB, con una clave aleatoria generada en cada PC. No se incluye una clave de vinculación ni una clave privada de firma en el repositorio o las descargas. No hay analítica ni un servidor remoto de vídeo. Las conexiones a Internet se usan para descargar las dependencias durante la instalación.

Autorizar depuración USB concede al PC acceso de desarrollo al dispositivo. Autoriza únicamente ordenadores de confianza y revoca la autorización desde Android cuando ya no la necesites. Consulta [SECURITY.md](SECURITY.md) para conocer los límites del modelo de seguridad.

El ejecutable de Windows y los scripts de TabLink no llevan firma Authenticode y Windows puede mostrar un aviso de editor desconocido. El APK lleva una firma propia del proyecto; el controlador VDD conserva la firma de su proveedor. Los hashes de cada release permiten comprobar la integridad de los archivos.

## Desinstalar

Ejecuta `Uninstall-TabLink.ps1` desde `%LOCALAPPDATA%\TabLink\app` para cerrar el compañero y quitar el inicio automático y el acceso directo. Para retirar también el VDD instalado por TabLink, abre PowerShell como administrador en esa carpeta y ejecuta:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-TabLink.ps1 -RemoveDriver
```

Los archivos y registros locales se conservan; puedes borrar `%LOCALAPPDATA%\TabLink` después de cerrar el programa. Desinstala la app Android desde sus ajustes y revoca la autorización de depuración si no la necesitas.

## Desarrollo

Java y MediaCodec en Android; C# y WinForms en Windows. Windows Graphics Capture y NVENC producen vídeo con imágenes independientes. La confirmación de presentación y las colas limitadas evitan acumular capturas antiguas.

Consulta [cómo compilar](docs/BUILDING.md), [el protocolo](docs/PROTOCOL.md) y [las pruebas](docs/VALIDATION.md). La compatibilidad con otras marcas se basa en negociación de capacidades; no se afirma haber probado todos los dispositivos. Audio, iOS, macOS, Linux, HDR, presión del lápiz y contenido protegido quedan fuera de esta versión.

El código de TabLink se publica bajo [licencia MIT](LICENSE). Las dependencias mantienen [sus propias licencias](THIRD_PARTY.md).

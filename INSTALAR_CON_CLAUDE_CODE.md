# Instalar Trading Hub con Claude Code

La forma más fácil de instalar Trading Hub es pedírselo a tu propio Claude Code: compila la app en tu Mac, la instala
en Aplicaciones y la deja conectada a tu suscripción de Claude. No necesitás saber programar.

## Antes de empezar

- Una Mac con Apple Silicon (M1 o más nuevo) y macOS 12 o más nuevo.
- [Claude Code](https://claude.com/claude-code) instalado y con tu sesión iniciada (plan Pro o Max).

## Pasos

1. Bajá el proyecto. Con Git:
   ```bash
   git clone https://github.com/ralphtradesz/trading-hub.git
   ```
   o desde GitHub: **Code → Download ZIP** y descomprimilo.
2. Abrí una Terminal en esa carpeta y arrancá Claude Code:
   ```bash
   cd trading-hub
   claude
   ```
3. Copiá y pegá este mensaje:

---

```
Instalá Trading Hub en mi Mac siguiendo las instrucciones de CLAUDE.md de esta carpeta:
revisá que mi Mac sea compatible, instalá el SDK de .NET 10 si falta (en ~/.dotnet, sin sudo),
compilá e instalá la app con ./build-app.sh --install, verificá que la app encuentre mi Claude Code
(creá el enlace en ~/.local/bin si hace falta), abrila y contame en pocas líneas cómo empiezo.
Si en Aplicaciones ya hay otra app llamada «Trading Hub», preguntame antes de reemplazarla.
```

---

## Qué va a pasar

- Claude Code revisa tu Mac (Apple Silicon, macOS 12 o más nuevo).
- Si no tenés el SDK de .NET 10, lo baja del sitio oficial de Microsoft a `~/.dotnet` (no toca el resto del sistema ni
  pide tu contraseña).
- Compila Trading Hub (unos minutos la primera vez) y lo instala en `/Applications/Trading Hub.app`.
- Verifica que la app encuentre tu Claude Code: como usa tu misma instalación y tu misma sesión, las funciones con IA
  (el chat de Backtesting, el laboratorio y el agente de riesgo de En vivo) quedan conectadas a **tu** suscripción, sin
  claves de API.
- Abre la app. Arranca con una cuenta simulada: nada opera dinero real hasta que lo habilites vos en Conexiones.

## Actualizar

Bajá la versión nueva (`git pull` o el ZIP nuevo) y pedile a Claude Code: «actualizá Trading Hub». Tus datos, tus
conexiones y tus estrategias quedan como estaban (viven fuera de esta carpeta).

## Si algo sale mal

Pedile a Claude Code que te muestre el error y lo resuelva. Los problemas más comunes:

- **La app no encuentra Claude Code**: pedile que cree el enlace en `~/.local/bin/claude`.
- **macOS dice que no puede verificar la app**: la app se firma "ad hoc" en tu Mac al compilarla; si la copiaste de otra
  computadora, compilala de nuevo en la tuya.
- **Falla la compilación**: confirmá que `~/.dotnet/dotnet --list-sdks` muestre una versión 10.

Más detalles en [docs/instalacion.md](docs/instalacion.md).

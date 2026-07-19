# Sistema de Chat Asíncrono por Sockets (TCP/IP)

![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet)
![C#](https://img.shields.io/badge/C%23-10.0-239120?style=flat&logo=c-sharp)
![License](https://img.shields.io/badge/License-MIT-blue.svg)

Sistema cliente-servidor de mensajería asíncrona y transferencia simultánea de archivos desarrollado en **C# / .NET 10** utilizando **Sockets TCP nativos** y una arquitectura de protocolo de tramas binarias (*Framing Protocol*).

---

## 📐 Diagrama de Arquitectura del Sistema

```mermaid
graph TD
    subgraph Clientes ["Clientes (ChatCliente)"]
        CA["Cliente A (WinForms + Sockets)"]
        CB["Cliente B (WinForms + Sockets)"]
    end

    subgraph Servidor ["Servidor Central (ChatServidor)"]
        L["TcpListener (Puerto 55000)"]
        R["Router de Tramas (RouteFrameAsync)"]
        M["Monitor UI & Logs (ServerMonitorForm)"]
        L --> R
        R --> M
    end

    subgraph Protocolo ["Capa de Protocolo (Chat.Protocol)"]
        FC["FrameCodec (Encoder / Decoder Binario)"]
        PL["Payloads JSON (Register, Edit, Delete, FileChunk)"]
    end

    CA <-->|"Sockets TCP / Tramas Binarias"| L
    CB <-->|"Sockets TCP / Tramas Binarias"| L
    R --- FC
    FC --- PL
```

---

## 🔄 Flujo del Protocolo de Tramas (Framing Protocol)

```mermaid
sequenceDiagram
    autonumber
    participant A as Cliente A (Jenny)
    participant S as Servidor (ChatServidor)
    participant B as Cliente B (Pedro)

    Note over A,S: 1. Registro e Identificación
    A->>S: Frame (Register: "Jenny")
    S->>A: Frame (RegistrationResult: Accepted, ID=1)
    S->>B: Broadcast (ClientList: [Jenny, Pedro])

    Note over A,B: 2. Mensajería en Tiempo Real
    A->>S: Frame (TextMessage -> TargetID=2: "Hola Pedro")
    S->>B: Frame (TextMessage -> SenderID=1: "Hola Pedro")

    Note over A,B: 3. Edición / Eliminación
    A->>S: Frame (EditMessage: MsgID, "Nuevo Texto")
    S->>B: Frame (EditMessage: MsgID, "Nuevo Texto")

    Note over A,B: 4. Transferencia de Archivos (Multiplexada)
    A->>S: Frame (FileStart: TransferID, "foto.jpg", 10MB)
    S->>B: Frame (FileStart)
    loop Bloques de 32 KB (Chunks)
        A->>S: Frame (FileChunk: TransferID, 32KB)
        S->>B: Frame (FileChunk: TransferID, 32KB)
    end
    A->>S: Frame (FileEnd: TransferID)
    S->>B: Frame (FileEnd: TransferID)
```

---

## ⚡ Multiplexación de Red (Envío Simultáneo)

```mermaid
flowchart LR
    subgraph Emisor ["ChatClient (Emisor)"]
        FA["Archivo A (Task 1)"]
        FB["Archivo B (Task 2)"]
        TX["Chat de Texto (Task 3)"]
    end

    SL{"sendLock (SemaphoreSlim)"}

    subgraph Cable ["Canal TCP Único (NetworkStream)"]
        NET["[Chunk 1-A] -> [Chunk 1-B] -> [Texto] -> [Chunk 2-A] -> [Chunk 2-B]"]
    end

    subgraph Receptor ["ChatClient (Receptor)"]
        R1["Reensamblado Archivo A"]
        R2["Reensamblado Archivo B"]
        RT["Render de Chat"]
    end

    FA --> SL
    FB --> SL
    TX --> SL
    SL --> NET
    NET --> R1
    NET --> R2
    NET --> RT
```

---

## 🚀 Características Principales

- **Arquitectura Cliente-Servidor sobre Sockets TCP:**
  - Comunicación asíncrona no bloqueante mediante `TcpListener`, `TcpClient` y `NetworkStream`.
  - Protocolo binario personalizado (`FrameCodec`) con cabeceras (*Headers*) y cargas de datos (*Payloads* JSON).

- **Transferencia Simultánea de Archivos (Multiplexación de Red):**
  - Envío paralelo de múltiples archivos pesados en bloques (*Chunks*) de 32 KB.
  - El canal TCP no se bloquea: podés seguir enviando mensajes o varios archivos a la vez.

- **Edición y Eliminación de Mensajes en Tiempo Real:**
  - Menú contextual (clic derecho sobre mensajes propios) para editar o eliminar mensajes.
  - Sincronización instantánea con todos los destinatarios.

- **Historial Persistente Local:**
  - Guardado automático de conversaciones en formato JSON en `%APPDATA%\ChatRedes\History`.
  - Recuperación de chats anteriores al volver a conectarse.

- **Control de Identidad y Deduplicación:**
  - El servidor valida y evita nombres de usuario duplicados o vacíos en tiempo real.

---

## 🛠️ Estructura del Proyecto

```text
proyecto-socket/
├── src/
│   ├── Chat.Protocol/       # Librería del protocolo binario (Frames, Codec, Payloads)
│   ├── Chat.Presentation/   # Componentes visuales compartidos y sistema de diseño
│   ├── ChatServidor/        # Aplicación Servidor con monitor visual y logs
│   └── ChatCliente/         # Aplicación Cliente (Formulario WinForms)
├── tests/
│   └── Chat.FunctionalTests/ # 87 Pruebas unitarias e integradas
└── publish/                 # Ejecutables compilados (.exe)
```

---

## 📋 Requisitos e Instalación

### Requisitos
- **SDK de .NET 10.0** o superior (para compilar desde código).
- Sistema Operativo Windows (WinForms).

### Ejecutar desde Consola

1. **Iniciar el Servidor:**
   ```bash
   dotnet run --project src/ChatServidor
   ```

2. **Iniciar el Cliente:**
   ```bash
   dotnet run --project src/ChatCliente
   ```

---

## 🧪 Pruebas Automatizadas

El proyecto incluye **87 pruebas unitarias y de integración** que validan la resistencia de la red, sanitización de archivos, resiliencia ante caídas y la interfaz de usuario.

Para ejecutar todas las pruebas:
```bash
dotnet test
```

---

## 📄 Licencia

Este proyecto fue desarrollado para el curso de **Redes de Computadoras I**. Libre uso educativo.

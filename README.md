# AzerothCore CPanel

Painel de controle para Windows feito para servidores [AzerothCore](https://www.azerothcore.org/)
(World of Warcraft 3.3.5a). Em vez de abrir o `authserver.exe` e o `worldserver.exe` na mão,
o painel cuida disso com um clique.

> **Status:** primeira versão (MVP) disponível.

## Download

Baixe o `AzerothCorePanel.exe` na [página de releases](https://github.com/EricksOliveira/AzerothCore-CPanel/releases/latest).
É um arquivo único e não precisa instalar o .NET.

1. Abra o `AzerothCorePanel.exe`
2. Clique em **Procurar...** e escolha a pasta da Build ou do Repack
3. Clique em **Start**

## Funcionalidades

### MVP
- [x] Escolher a pasta da Build ou do Repack; o painel encontra o `authserver.exe` e o `worldserver.exe`
- [x] **Start**: inicia o Authserver e depois o Worldserver
- [x] **Stop**: desligamento limpo (`server shutdown 1`), sem perder dados dos personagens
- [x] **Restart**: para e inicia os dois servidores
- [x] Logs ao vivo de cada processo, em abas
- [x] Campo para enviar comandos ao console do worldserver
- [x] Reinicia o worldserver sozinho quando ele pede restart (`server restart`, código de saída 2)
- [x] Se o painel fechar ou travar, os servidores não ficam rodando escondidos (Job Object)

### Próximas versões
- [ ] Auto-restart em caso de crash
- [ ] Iniciar e parar o MySQL do Repack
- [ ] Ícone na bandeja do sistema e opção de iniciar junto com o Windows
- [ ] Uptime, uso de CPU e RAM de cada processo
- [ ] Jogadores online
- [ ] Console para enviar comandos GM (via SOAP)
- [ ] Backup agendado do banco de dados
- [ ] Editor do `worldserver.conf` e do `authserver.conf`

## Requisitos

- Windows 10 ou superior
- `Console.Enable = 1` no `worldserver.conf` (padrão). Sem o console, o Stop não consegue pedir um
  desligamento limpo e encerra o processo à força depois do tempo limite.
- Para compilar: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Como funciona

- O painel inicia cada servidor com a pasta do `.exe` como pasta de trabalho, para o AzerothCore achar a
  pasta `configs`.
- **Stop** envia `server shutdown 1` ao worldserver e espera ele fechar (60 segundos por padrão). Se não
  fechar, o processo é encerrado à força. O authserver é encerrado direto, porque não guarda dados de jogo.
- As configurações ficam em `%APPDATA%\AzerothCorePanel\settings.json`.

## Como compilar

```powershell
dotnet build src/AzerothCorePanel
```

Para gerar um único `.exe` que roda sem instalar o .NET:

```powershell
dotnet publish src/AzerothCorePanel -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Licença

[MIT](LICENSE)

<p align="center"><img src="assets/logo.png" alt="AzerothCore CPanel" width="160"></p>

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
- [x] Auto-restart em caso de crash, com limite de tentativas para não ficar em loop
- [x] Ícone na bandeja do sistema: minimiza para perto do relógio, menu com Start/Stop/Restart e avisos quando
  um servidor cai
- [x] Iniciar o painel com o Windows (já na bandeja), com opção de iniciar os servidores depois que o MySQL
  estiver pronto

### Próximas versões
- [ ] Iniciar e parar o MySQL do Repack
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
- **Reiniciar servidor se cair** (ligado por padrão): se um servidor fechar sem ninguém pedir, o painel o
  inicia de novo depois de 5 segundos. Depois de 3 quedas em 5 minutos ele desiste, porque o problema
  provavelmente é de configuração ou do banco de dados. Um `server shutdown` dado no jogo ou no console não
  conta como queda.
- **Bandeja do sistema**: o ícone fica perto do relógio enquanto o painel está aberto. Clique nele para abrir o
  painel; o botão direito mostra Start, Stop, Restart e Sair. Com **Minimizar para a bandeja** ligado (padrão),
  minimizar tira o painel da barra de tarefas. Quando um servidor cai, aparece uma notificação. No Windows 11 o
  ícone começa na área de ícones ocultos (a seta ^ ao lado do relógio); arraste-o para a barra para deixá-lo
  sempre visível.
- **Iniciar com o Windows**: com **Iniciar o painel com o Windows** ligado, o painel abre sozinho quando você entra
  no Windows, direto na bandeja. Com **E iniciar os servidores**, ele espera o MySQL aceitar conexões (até 3
  minutos, lendo o endereço do `LoginDatabaseInfo` no `worldserver.conf`) e inicia os servidores. A opção usa a
  chave `Run` do registro do seu usuário, sem precisar de administrador.
- Só um painel roda por vez: abrir de novo traz a janela que já está aberta (ou na bandeja).
- As configurações ficam em `%APPDATA%\AzerothCorePanel\settings.json`.

## Como compilar

```powershell
dotnet build src/AzerothCorePanel
```

Para gerar um único `.exe` que roda sem instalar o .NET:

```powershell
dotnet publish src/AzerothCorePanel -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## Licença

[MIT](LICENSE)

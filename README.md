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
- [x] Uso de CPU e RAM de cada servidor, atualizado a cada segundo
- [x] Jogadores online: total no status e na bandeja, e aba com nome, nível, raça, classe, facção e mapa
- [x] Controle do MySQL: status, iniciar e parar (MySQL portátil do Repack ou serviço do Windows), e iniciar o
  MySQL junto com o Start
- [x] Comandos GM via SOAP: aba com conta GM, atalhos, histórico, e "Expulsar" na lista de jogadores

### Próximas versões
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
- **CPU e RAM**: ao lado do status de cada servidor. A CPU soma todos os núcleos (100% = todos ocupados, como no
  Gerenciador de Tarefas). A RAM é a memória física em uso (working set); passe o mouse para ver o pico.
- **Jogadores online**: enquanto o worldserver está online, o painel consulta o banco de personagens a cada 15
  segundos (usando o `CharacterDatabaseInfo` do `worldserver.conf`). O total aparece no status e na bandeja, e a
  aba **Jogadores** mostra a lista.
- **MySQL**: o painel procura um `mysqld.exe` dentro da pasta do servidor (MySQL portátil, comum em Repacks) e,
  se não achar, um serviço do Windows de MySQL ou MariaDB. A linha **MySQL** no status mostra o estado e tem os
  botões **Iniciar** e **Parar** (o Parar só fica ativo com os servidores parados). Com **Iniciar o MySQL junto
  com os servidores** (padrão), o Start inicia o MySQL primeiro se ele estiver parado. O Stop não desliga o MySQL.
  - MySQL portátil: é iniciado com o `my.ini` da pasta dele e desligado com `SHUTDOWN`, usando a conta do
    `LoginDatabaseInfo`. Ele continua rodando se o painel fechar, para não ser interrompido no meio de uma escrita.
  - Serviço do Windows: iniciar e parar serviços precisa de administrador; se o painel não estiver como
    administrador, o Windows pede permissão (UAC).
- **Comandos GM (SOAP)**: a aba **Comandos GM** executa comandos pelo SOAP do worldserver e mostra a resposta de
  cada um. Funciona mesmo com o worldserver aberto fora do painel. Precisa de `SOAP.Enabled = 1` no
  `worldserver.conf` (o botão **Ativar SOAP** muda só essa linha; reinicie o worldserver depois) e de uma conta
  do jogo com GM nível 3. A senha fica salva criptografada para o seu usuário do Windows (DPAPI). Tem atalhos
  (info do servidor, salvar personagens, anunciar, expulsar, criar conta, dar GM, recarregar config), histórico
  com as setas e, na aba **Jogadores**, o clique direito expulsa um jogador.
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

## Dependências

- [MySqlConnector](https://mysqlconnector.net/) (MIT), para consultar o banco de personagens.

## Licença

[MIT](LICENSE)

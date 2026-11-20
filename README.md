# AzerothCore CPanel

Painel de controle para Windows feito para servidores [AzerothCore](https://www.azerothcore.org/)
(World of Warcraft 3.3.5a). Em vez de abrir o `authserver.exe` e o `worldserver.exe` na mão,
o painel cuida disso com um clique.

> **Status:** em desenvolvimento inicial. Ainda não há uma versão para usar.

## Funcionalidades planejadas

### MVP
- [ ] Configurar a pasta da Build ou do Repack (caminhos do `authserver.exe` e do `worldserver.exe`)
- [ ] **Start**: inicia o Authserver e depois o Worldserver
- [ ] **Stop**: desligamento limpo (`server shutdown`), sem perder dados dos personagens
- [ ] **Restart**: reinicia os servidores automaticamente
- [ ] Logs ao vivo de cada processo

### Próximas versões
- [ ] Auto-restart em caso de crash (usando o código de saída do worldserver)
- [ ] Iniciar e parar o MySQL do Repack
- [ ] Ícone na bandeja do sistema e opção de iniciar junto com o Windows
- [ ] Uptime, uso de CPU e RAM de cada processo
- [ ] Jogadores online
- [ ] Console para enviar comandos GM (via SOAP)
- [ ] Backup agendado do banco de dados
- [ ] Editor do `worldserver.conf` e do `authserver.conf`

## Requisitos

- Windows 10 ou superior
- Para compilar: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

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

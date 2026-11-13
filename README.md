# GitHub Actions Monitor

Widget discreto para o canto da tela do Windows que mostra, em tempo real, o que está rodando no GitHub Actions:

- ▶ execuções **rodando** com tempo decorrido e **previsão de término** (mediana das últimas 10 execuções com sucesso)
- ⏳ **fila**: runs aguardando + jobs aguardando runner + aguardando **aprovação** de environment
- **branch de → para** (`feat/x → main (PR #7)`), evento (`push`, `pull_request`...) e quem disparou
- selo **DEPLOY** (com o environment, ex.: `DEPLOY PRODUCTION`)
- **CPU e RAM** dos runners self-hosted, status online/ocupado/offline
- alerta de **job travado na fila** (padrão 5 min) só quando ninguém está pegando o job (runner offline). Se o runner self-hosted está ocupado com outro job, aparece **RUNNER OCUPADO** (fila normal, sem notificação)
- notificação quando um **deploy termina** ou quando **qualquer run falha**
- clique num item para abrir no GitHub · `Ctrl+Alt+G` mostra/esconde · arrastável · fica semitransparente quando ocioso
- inicia com o Windows · ~120–170 MB de RAM (WPF) · fora da barra de tarefas e do Alt+Tab
- **log de erros** em `%LOCALAPPDATA%\GhActionsMonitor\logs\app.log` (bandeja → *Abrir log de erros*)

## Instalação (qualquer PC)

```bat
git clone https://github.com/SEU-USUARIO/gh-actions-monitor
cd gh-actions-monitor
install.cmd
```

O `install.cmd` não precisa de administrador. Ele instala o .NET 8 SDK via `winget` se faltar, compila, copia para `%LOCALAPPDATA%\Programs\GhActionsMonitor`, registra para iniciar com o Windows e abre o app.

Sem querer compilar? Baixe o `.zip` em **Releases** (gerado pelo workflow `release.yml` ao criar uma tag `v*`). O `-standalone` roda sem instalar nada.

## Autenticação: usa a credencial que o PC já tem

Nenhuma configuração na maioria dos casos. O app tenta, em ordem:

1. variável `GH_TOKEN` / `GITHUB_TOKEN`
2. token salvo pelo próprio app (Cofre de Credenciais do Windows)
3. `gh auth token` (GitHub CLI)
4. `git credential fill`, ou seja, o **Git Credential Manager** que vem com o Git para Windows. Se você já faz `git push` por HTTPS nesse PC, já funciona.

> ⚠️ **Chave SSH não serve.** A API REST do GitHub não aceita SSH. Se o PC só usa SSH, rode `gh auth login` uma vez, ou cole um token quando o app pedir. Ele fica no Cofre de Credenciais, nunca em arquivo.

Token clássico: escopo `repo`. Fine-grained: leitura em *Actions*, *Administration* (listar runners), *Deployments* e *Metadata*.

## Configuração

Arquivo: `%APPDATA%\GhActionsMonitor\config.json`. Abra pelo ícone da bandeja → *Abrir configuração*, edite e depois use *Aplicar configuração*. Todas as opções estão em [`config.example.json`](config.example.json).

Por padrão monitora os **10 repositórios com push mais recente** a que você tem acesso (seus, de organizações e de colaboração). Para fixar uma lista, use `"repos": ["usuario/repo"]`.

## CPU/RAM dos runners

| Runner | Como funciona |
|---|---|
| GitHub-hosted (`ubuntu-latest`...) | O GitHub **não expõe** métricas. Aparece só o rótulo `hosted:ubuntu-latest`. |
| Self-hosted **no mesmo PC** do monitor | Automático, sem instalar nada (se o nome do runner = nome do PC). |
| Self-hosted em **outra máquina** | Instale o agente nela (Python 3, só biblioteca padrão): |

```bash
# Linux (systemd)
sudo agent/install-linux.sh 9465 [token-opcional]
```
```powershell
# Windows (admin)
powershell -ExecutionPolicy Bypass -File agent\install-windows.ps1 -Port 9465
```

O monitor procura o agente em `http://<nome-do-runner>:9465`. Se o nome do runner não resolver na rede, mapeie em `"agents": { "nome-do-runner": "http://IP:9465" }`. O agente expõe só CPU/RAM agregados, mas em rede não confiável use `--token` (e `agentToken` na config) ou VPN.

## Limites conhecidos (honestos)

- **Rate limit**: 5.000 req/h por token. Com ETag, ciclos sem mudança não contam. 10 repos ociosos usam na prática bem pouco. Se estourar, o rodapé mostra quando volta.
- Lista as 30 execuções mais recentes por repo. Um run na fila atrás de 30 runs mais novos não aparece (raro).
- Detecção de deploy: pelo nome (regex `deployPattern`) **ou** por um *deployment* criado para o commit. Workflows de deploy sem `environment:` e sem "deploy" no nome não são detectados.
- Repos descobertos automaticamente = os com **push** recente. Repo só com workflow agendado (cron) e sem push precisa estar em `repos`.
- Branch de destino só existe para PR do mesmo repositório (PR de fork não traz essa info na API).

## Log e solução de problemas

Bandeja → **Abrir log de erros**, ou `%LOCALAPPDATA%\GhActionsMonitor\logs\app.log`. O log mostra a credencial usada (nunca o token), quais repos estão sendo monitorados, falhas de API e exceções da interface. Ele gira em 1 MB e junta erros repetidos numa linha só.

Se a interface entrar em laço de erro, o app para de redesenhar em vez de travar com CPU a 100%, mostra *Erro de interface* no rodapé e grava o motivo no log.

## Desinstalar

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1 -Purge
```

## Desenvolvimento

```bash
dotnet build src/GhActionsMonitor -c Release
```
Compila também em Linux/macOS (`EnableWindowsTargeting`), mas só roda no Windows.

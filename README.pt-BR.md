# RunnerWatch

[![CI](https://github.com/GustavoDanielL/runnerwatch/actions/workflows/ci.yml/badge.svg)](https://github.com/GustavoDanielL/runnerwatch/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/GustavoDanielL/runnerwatch?sort=semver)](https://github.com/GustavoDanielL/runnerwatch/releases/latest)
[![Licença: MIT](https://img.shields.io/badge/licen%C3%A7a-MIT-blue)](LICENSE)

Um widget de desktop que mostra as execuções do GitHub Actions dos seus repositórios: o que está rodando, o que está na fila e o que está travado. Também mostra CPU e RAM dos runners self-hosted. Windows, macOS e Linux.

<img src="docs/media/demo.gif" width="360" alt="RunnerWatch em modo demo: um deploy rodando, um job na fila atrás de um runner ocupado, uma release travada e uma execução com falha">

[English](README.md)

## Instalação

Windows (PowerShell):

```powershell
irm https://raw.githubusercontent.com/GustavoDanielL/runnerwatch/main/install.ps1 | iex
```

macOS e Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/GustavoDanielL/runnerwatch/main/install.sh | sh
```

Scoop:

```powershell
scoop install https://raw.githubusercontent.com/GustavoDanielL/runnerwatch/main/packaging/scoop/runnerwatch.json
```

Os scripts instalam para o usuário atual (sem administrador) e conferem o arquivo baixado com o `SHA256SUMS.txt` da release. Também dá para baixar o arquivo direto em [Releases](https://github.com/GustavoDanielL/runnerwatch/releases/latest).

Os binários não têm assinatura de código. No Windows, o SmartScreen pede confirmação na primeira vez (Mais informações → Executar assim mesmo). No macOS, instale com o comando `curl` acima; um zip baixado pelo navegador é bloqueado pelo Gatekeeper porque o app não é notarizado.

Para experimentar sem conectar ao GitHub:

```bash
RunnerWatch --demo
```

## O que aparece

- Execuções rodando, com tempo decorrido e uma estimativa baseada na mediana das últimas 10 execuções com sucesso do mesmo workflow.
- Execuções na fila, jobs esperando runner e execuções aguardando aprovação de environment.
- Dois tipos de espera, separados:
  - **RUNNER OCUPADO**: o job espera porque o seu runner self-hosted está trabalhando em outro job. Sem notificação.
  - **TRAVADO**: nenhum runner pegou o job em 5 minutos (configurável). Envia notificação.
- Deploys, detectados pelo nome do workflow ou por um deployment criado para o commit. Uma notificação avisa quando terminam.
- Execuções com falha, por 15 minutos. Clique com o botão direito para re-executar os jobs que falharam.
- Runners self-hosted, com status e CPU/RAM.

Clique numa execução para abri-la no GitHub. No Windows, `Ctrl+Alt+G` mostra e esconde o widget; no macOS e no Linux, use o ícone da bandeja. O idioma (português ou inglês) e o tema claro/escuro seguem o sistema.

Tamanho e posição:

- Arraste o cabeçalho para mover. A borda de baixo fica fixa, então o widget cresce para cima quando aparecem execuções.
- Arraste a borda esquerda ou direita para mudar a largura. `Ctrl` + roda do mouse, ou bandeja → Tamanho, aumenta ou diminui tudo.
- O botão `▾` recolhe o widget a uma linha (bolinha de status e contadores) acima da barra de tarefas; `▴` expande de novo. As notificações continuam chegando com ele recolhido.

As três coisas ficam salvas entre as sessões.

## Autenticação

O RunnerWatch usa a primeira credencial que funcionar, nesta ordem:

1. `GH_TOKEN` ou `GITHUB_TOKEN`
2. um token salvo pelo app, guardado no cofre do sistema (Gerenciador de Credenciais do Windows, Keychain do macOS, Secret Service no Linux)
3. `gh auth token`, do [GitHub CLI](https://cli.github.com/)
4. `git credential fill`, que cobre o Git Credential Manager no Windows

Se nenhuma funcionar, ele pede um token. Chave SSH não serve: a API REST do GitHub não aceita.

Permissões: token clássico precisa do escopo `repo`. Token fine-grained precisa de leitura em Actions, Administration (para listar runners), Deployments e Metadata, e de escrita em Actions para re-executar jobs.

## Configuração, métricas e limitações

Estão no [README em inglês](README.md#configuration) e em [docs/configuration.md](docs/configuration.md): todas as opções, onde ficam os arquivos em cada sistema, o agente de métricas para runners em outras máquinas e os limites conhecidos.

## Contribuindo

Veja o [CONTRIBUTING.md](CONTRIBUTING.md). Issues e PRs em português são bem-vindos. Com o `--demo` dá para mexer na interface sem token do GitHub, e traduzir para outro idioma é um único arquivo JSON.

## Licença

[MIT](LICENSE). O RunnerWatch não tem vínculo com o GitHub.

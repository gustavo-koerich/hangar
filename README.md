# Hangar

[![build](https://github.com/gustavo-koerich/hangar/actions/workflows/build.yml/badge.svg)](https://github.com/gustavo-koerich/hangar/actions/workflows/build.yml)

Painel de desktop para quem mantém muitos projetos na mesma máquina. O Hangar varre a pasta de projetos e mostra, em cartões, o que cada um é, em que situação está, o estado do git e do Docker, e oferece atalhos para ligar, desligar, abrir no VS Code ou abrir o link de teste.

## Como funciona

- **Fichas**: cada projeto tem um `hangar.yaml` na própria pasta (nome, resumo, área, tecnologia, situação, links, comandos). É a única coisa escrita à mão — o Hangar não tem banco de dados, então as fichas viajam junto com o projeto. Veja [exemplos/meu-projeto/hangar.yaml](exemplos/meu-projeto/hangar.yaml).
- **Fatos automáticos**: última alteração (ignorando `node_modules`, `.venv`, `bin`, `obj`…), status e remoto do git, containers do Docker associados ao projeto e se a porta do link de teste está respondendo.
- **Situação calculada**: arquivado › no ar › em desenvolvimento (alterado nos últimos 30 dias) › parado.
- **Ligar/desligar**: roda os comandos da ficha em segundo plano e só marca "ligado" quando o link responde HTTP; ao desligar, encerra apenas o processo dono da porta, nunca processos do sistema ou do Docker.
- **Alertas**: projetos arquivados há mais de 30 dias geram um aviso — o Hangar **nunca exclui nada sozinho**.

## Destaques técnicos

- **WPF + WebView2**: casca nativa em C# com interface em HTML/CSS/JS puro, servida por mapeamento de host virtual (`https://hangar.local`)
- **Ponte segura** entre tela e C# via `postMessage`: a interface só envia o id do projeto, que é validado contra o catálogo; links só abrem se estiverem na ficha
- **Distribuição** como `.exe` único e autocontido (não precisa de .NET instalado), com tema claro/escuro seguindo o Windows

## Stack

C# · .NET 8 · WPF · WebView2 · YamlDotNet · HTML/CSS/JS

## Rodando

Requer Windows 10/11 e .NET 8 SDK. O Hangar lista as subpastas da pasta que contém a pasta `hangar/` (ou `Documentos\Projetos`).

```powershell
dotnet build src/Hangar
dotnet run --project src/Hangar
```

Para gerar o executável autocontido e um atalho na Área de Trabalho: `.\publicar.ps1`.

## Licença

MIT

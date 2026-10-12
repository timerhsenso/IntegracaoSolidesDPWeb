# Novidades — IntegracaoSolidesDP

Uma seção por versão, a mais nova primeiro. O título é `## <versão> — <data>`: o `scripts\publicar-windows.ps1`
só gera o pacote de uma versão que tenha a sua seção aqui, e a Web mostra este arquivo em Ajuda › Novidades.
Escreva para quem usa: o que mudou na tela e no comportamento, não o código.

## 1.0.2 — 12/10/2026

- Avisos por e-mail (Administração › Notificações): falha da integração na hora, lembrete enquanto continuar, aviso de que voltou ao normal e resumo diário das pendências. Destinatários gerais ou por empresa.
- Novo status "Concluída com pendências" (amarelo): nada falhou, mas há cadastro para o RH resolver. "Concluída com erros" (vermelho) passa a ser só falha de verdade.
- Empresa (CNPJ) que não existe no Sólides DP vira pendência, e não falha.
- Relatório de vinculação na tela, no detalhe de cada simulação, com filtros por resultado e o cargo e o nome atuais no Sólides DP.
- "Só no Sólides DP": quem está ativo no DP sem colaborador ativo com o mesmo CPF no RHSenso, dizendo onde o CPF está no RHSenso.
- Trava de desligamentos por execução (padrão 20), na tela Configuração.
- Colaborador recusado pelo Sólides DP 3 vezes com os mesmos dados vira pendência "recusado_pelo_dp" e para de ser reenviado a cada execução; tenta de novo quando os dados mudam ou uma vez por dia.
- Limpeza automática do histórico (execuções 180 dias, arquivos de relatório 30 dias), configurável.
- Sinal de vida do serviço: o Painel mostra cada serviço ligado ao banco e avisa "Serviço sem resposta".
- Ajuda › Novidades (esta página).

## 1.0.1 e anteriores

- Serviço de sincronização RHSenso → Sólides DP: cargos, locais de trabalho, colaboradores, desligamentos e férias.
- Uma conta do Sólides DP por empresa, com a identidade do colaborador pelo CPF (vincula quem o RH já tinha cadastrado no DP).
- Aplicação Web de gestão: Painel, Execuções, O que foi migrado, Pendências, Pedidos ao serviço, Configuração versionada, Empresas (token, filiais, piloto, simulação), Usuários e Auditoria.
- Simulação (dry-run) com relatório de vinculação em CSV; quem está desligado no Sólides DP não é criado de novo.
- Instalação do serviço como serviço do Windows e da Web no IIS; manual de uso na Web e manual técnico em PDF.
- Versão da Web e do serviço no rodapé do menu.

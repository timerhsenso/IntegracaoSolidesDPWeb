# Novidades — IntegracaoSolidesDP

Uma seção por versão, a mais nova primeiro. O título é `## <versão> — <data>`: o `scripts\publicar-windows.ps1`
só gera o pacote de uma versão que tenha a sua seção aqui, e a Web mostra este arquivo em Ajuda › Novidades.
Escreva para quem usa: o que mudou na tela e no comportamento, não o código.

## 1.0.6 — 14/10/2026

### Novidades
- Conferência DP x RHSenso mostra também quem está ativo no Sólides DP sem colaborador no RHSenso ("Só no Sólides DP"),
  com o que o RHSenso diz do CPF e o cargo no DP. Com o piloto preenchido, esta parte não aparece.
- Ignorar: na conferência, marque quem está só no Sólides DP e não vai para a folha (ex.: coordenadores contratados
  direto no DP) e use "Ignorar os marcados". A pessoa some das próximas conferências e do relatório de vinculação da
  simulação. Vale por empresa; nada muda no Sólides DP (perfis Operador e Admin; auditoria IGNORADO_INCLUIDO).
- Tela Empresas: quadro "Ignorados na conferência", com quem ignorou e quando, o botão "Voltar a validar" e o histórico
  de quem voltou.
- Quem estava ignorado e aparece ativo na folha da empresa sai da lista sozinho (aviso "ignorado_voltou" na execução) e
  segue as regras normais: se for de um tipo que a integração não leva, como estagiário, continua sem ir.

### Melhorias
- Tela da empresa em uma coluna, na ordem de importância (integração, token, ignorados, filiais, piloto, conta do
  Sólides DP, pedidos), com a barra "Salvar a empresa" fixa no rodapé enquanto se rola a tela.
- Botão ☰ no topo de todas as telas: no computador recolhe o menu lateral e o conteúdo usa a largura toda; a escolha
  fica guardada no navegador.

### Segurança
- Correções da revisão de 14/10/2026:
  - textos vindos do Sólides DP e do RHSenso (como o nome do colaborador) passam a ser tratados também dentro dos
    atributos das telas, e não podem mais executar código no navegador de quem abre a tela;
  - CSV exportado (conferência, relatório de vinculação, itens da execução) não executa fórmulas ao abrir no Excel;
  - a tela de login não revela quais usuários existem, estão desativados ou bloqueados (a auditoria continua
    registrando o motivo);
  - as chaves do cookie de login passam a ser gravadas cifradas pelo Windows. Depois de instalar, todos precisam
    entrar de novo;
  - permissoes-sql.sql: o login do serviço deixa de ler as tabelas de login da Web.

## 1.0.5 — 14/10/2026

### Novidades
- Agenda das execuções pela Web (Regras gerais): a cada N minutos ou em horários fixos, e os dias da semana, para todas
  as empresas. O serviço segue a agenda nova sem reiniciar. "Como no arquivo do serviço" mantém o appsettings.json.

### Correções
- Conferência DP x RHSenso: CTPS, série, telefone, cargo e local apareciam como "vazio" no Sólides DP mesmo estando
  preenchidos lá. A lista de colaboradores da API vem resumida; a conferência agora lê o cadastro completo de cada
  colaborador vinculado, e o local é lido de onde a API realmente o devolve.

### Segurança
- Pacotes do .NET atualizados para a 10.0.13 (correções da Microsoft de 13/10/2026), incluindo
  System.Security.Cryptography.Pkcs e System.Security.Cryptography.Xml.

### Observação para as empresas
- A API do Sólides DP não devolve o CNPJ das empresas da conta. Em conta com uma empresa só, use em Empresas › Editar
  "Empresa no Sólides DP = Não enviar empresa"; com "Procurar pelo CNPJ", o envio real fica bloqueado
  (company_not_found).

## 1.0.4 — 12/10/2026

- Instalação: o cliente escolhe a pasta. Na primeira instalação, os instaladores do serviço e da Web perguntam a pasta (Enter aceita a sugerida) ou recebem -InstallDir. Nas atualizações usam a pasta onde o sistema já está.
- O instalador recusa instalar na própria pasta onde o zip foi extraído (o zip da versão seguinte sobrescreveria a configuração).
- Desinstalar com -RemoveFiles apaga a pasta onde o sistema está instalado, e não a pasta padrão.

## 1.0.3 — 12/10/2026

Conferência DP x RHSenso e histórico de versões.

- Conferência DP x RHSenso (menu Conferência DP x RHSenso): para cada colaborador ativo e vinculado, compara o que a integração enviaria hoje com o que está no Sólides DP — nome, PIS, CTPS e série, nascimento, admissão, e-mail, telefone, sexo, cargo, local de trabalho, empresa e situação (ativo no RHSenso e desligado no DP, ou cadastro que sumiu do DP). Só lê o Sólides DP; não grava nada lá.
- Não entram na conferência o que é do Sólides DP por regra (vigência, escala, regra de ponto e Código Externo) nem campo vazio no RHSenso. Espaços e maiúsculas no nome, pontuação no PIS e no telefone e o formato da data não contam como diferença.
- Roda toda semana (padrão: segunda-feira, 6h; dia, hora e liga/desliga na tela Configuração) e pelo novo pedido "Conferir campos (DP x RHSenso)" em Pedidos ao serviço, ou pelo botão "Conferir agora" da própria tela.
- Na conferência: grade com uma linha por campo diferente (valor no RHSenso e no Sólides DP), filtro por campo e CSV para baixar.
- "Reenviar do RHSenso": marque quem deve ficar como está no RHSenso e a próxima execução reenvia o cadastro inteiro (perfis Operador e Admin; fica na auditoria como REENVIO_SOLICITADO). Se o correto é o Sólides DP, corrija o RHSenso.
- O Painel avisa quando a última conferência de uma empresa encontrou diferenças. Na conferência semanal, quem recebe o resumo diário daquela empresa recebe a lista por e-mail.
- As conferências antigas entram na limpeza do histórico (a última de cada empresa fica).
- Ajuda › Novidades: o que mudou em cada versão, com a versão instalada na Web e no serviço destacada. A versão no rodapé do menu abre esta página.
- Toda versão nova precisa da sua seção neste arquivo: sem ela, o pacote não é gerado. O arquivo vai junto nos pacotes do serviço e da Web.
- Leitura mais tolerante dos dados que o Sólides DP devolve (número ou texto, objeto ou só o id), para um formato diferente não derrubar a leitura do cadastro.

## 1.0.2 — 12/10/2026

Avisos por e-mail, pendências separadas de erros, relatório de vinculação na tela, limpeza do histórico e sinal de vida do serviço.

### Avisos por e-mail
- Nova tela Administração › Notificações (só Admin): servidor de e-mail (SMTP) com a senha gravada cifrada e nunca exibida, destinatários gerais ou por empresa, e o que cada um recebe (falhas, resumo ou os dois).
- Falha: o aviso sai na hora em que a empresa passa de OK para falha, e não a cada execução. Se continuar, sai um lembrete (padrão: 24 horas). Quando volta a funcionar, sai "voltou ao normal", dizendo se ficaram pendências.
- Resumo diário das pendências de cadastro (padrão: 8h, só em dias úteis), só com as novas desde o último resumo.
- Simulações não avisam, a não ser que isso seja ligado na tela.
- Botão "Enviar teste" (pedido ao serviço) e a lista dos últimos envios, com o erro de cada um.

### Status das execuções
- Novo status "Concluída com pendências" (amarelo): nada falhou, mas há cadastro para o RH resolver (CPF desligado no DP, Código Externo em uso, CNPJ sem empresa no DP, recusado pelo DP).
- "Concluída com erros" (vermelho) passa a ser só falha de verdade (recusa do Sólides DP, trava de segurança).
- Empresa (CNPJ) que não existe no Sólides DP vira pendência, e não falha.
- O Painel conta as execuções com pendências junto com as com erros, no gráfico de 14 dias.

### Relatório de vinculação
- O relatório da simulação aparece na tela, no detalhe da execução, com filtros por resultado: Pendência, Só no Sólides DP, Desligar, Criar, Vincular pelo CPF, Atualizar, Fora do escopo e Sem alteração.
- Novo resultado "Só no Sólides DP": quem está ativo no DP sem colaborador ativo com o mesmo CPF no RHSenso. O CPF é procurado em todo o RHSenso, e a observação diz onde está (desligado, transferido, outra empresa, outro tipo de colaborador) ou que não existe lá. A integração não desliga esses cadastros: o RH decide.
- Nova linha "Desligar": quem a integração vai desligar no Sólides DP.
- Cargo e nome atuais no Sólides DP para todos, inclusive para quem já está vinculado, e aviso quando o cargo vai mudar ou o nome não confere.

### Segurança e reenvio
- Trava de desligamentos por execução (padrão: 20): acima disso, ninguém é desligado e fica uma falha. Na simulação, só um aviso.
- Colaborador recusado pelo Sólides DP 3 vezes seguidas com os mesmos dados vira pendência "recusado_pelo_dp" e para de ser reenviado a cada execução. Tenta de novo quando os dados mudam no RHSenso, ou uma vez por dia (caso a correção tenha sido feita no DP). Queda do DP, demora e erro 5xx não contam. O limite fica na tela Configuração.

### Histórico e serviço
- Limpeza diária do histórico: execuções com mais de 180 dias (com itens e relatórios), e-mails enviados e arquivos de relatório com mais de 30 dias. Nunca apaga a última execução de cada empresa, os vínculos, a auditoria nem os pedidos. Prazos e hora configuráveis; 0 = não apaga.
- Sinal de vida do serviço: o Painel mostra cada máquina que roda o serviço com este banco (versão, último sinal, "Respondendo" ou "Sem resposta") e avisa "Serviço sem resposta" no Painel e nas telas de pedidos. Uma máquina inesperada na lista indica outro serviço usando o mesmo banco.
- Correções: o pedido "Testar e-mail" não era aceito pela tela de pedidos.

## 1.0.1 e anteriores

Primeira versão em homologação: o serviço de sincronização e a Web de gestão.

### Sincronização RHSenso → Sólides DP
- Envia cargos, locais de trabalho (filiais), colaboradores, desligamentos e férias, nessa ordem, a cada 30 minutos (ou em horários fixos).
- Uma conta do Sólides DP por empresa, cada uma com o seu token.
- Identidade do colaborador pelo CPF: quem o RH já tinha cadastrado no DP é vinculado pelo CPF, e não duplicado.
- Quem está desligado no Sólides DP não é criado de novo: vira pendência "cpf_desligado_no_dp" até o RH readmitir lá.
- Código Externo de quem já existe no DP é respeitado; nos novos, vai a matrícula. Se a matrícula já for o Código Externo de outra pessoa, a criação fica bloqueada ("codigo_externo_em_uso").
- Mantém a vigência que já está no Sólides DP, a escala atribuída lá, e envia a data de início no cargo quando o cargo muda.
- Simulação (dry-run): faz toda a análise sem gravar nada no DP, com relatório de vinculação em CSV.
- Travas de segurança: máximo de colaboradores novos e de férias canceladas por execução; nada é enviado se o RHSenso devolver 0 colaboradores; uma execução por empresa de cada vez.
- Férias: período enviado como lançamento de ajuste; reprogramadas ou apagadas no RHSenso são canceladas no DP; desistência depois de 5 falhas do mesmo período.

### Aplicação Web de gestão
- Login com perfis Consulta, Operador e Admin; usuários e auditoria de tudo que é feito.
- Painel, Execuções (com os itens de cada uma), O que foi migrado, Pendências e Pedidos ao serviço (executar, simular, verificar configuração, consultar o Sólides DP, conferir enviados).
- Configuração versionada: cada gravação é uma versão nova, com motivo obrigatório e histórico. Ativar e desativar a integração pelo Painel.
- Tela Empresas: empresa habilitada, simulação ou envio real, go-live, filiais marcadas, piloto, token do Sólides DP (gravado cifrado), escala, regra de ponto e empresa no DP. Regras gerais separadas das regras de cada empresa.
- Ajuda com o manual de uso de cada tela e o manual técnico em PDF (só Admin).
- Versão da Web e do serviço no rodapé do menu, com alerta quando são diferentes.
- Caixa de diálogo padrão do sistema para confirmações e motivos.

### Instalação
- Serviço do Windows (install-service.ps1) e Web no IIS (install-web.ps1), com scripts de permissões no SQL Server (logins próprios para o serviço e para a Web). As atualizações preservam o appsettings.json instalado.
- Pacotes self-contained gerados por scripts\publicar-windows.ps1 (não precisa instalar o .NET no servidor).
// Funções comuns das telas: grades (DataTables server-side), rótulos e a caixa de diálogo padrão.
(function () {
    'use strict';

    const rotulos = window.rotulos || { status: {}, entidades: {}, acoes: {}, comandos: {}, origens: {} };

    const idioma = {
        processing: 'Carregando...',
        search: 'Buscar:',
        lengthMenu: '_MENU_ por página',
        info: '_START_ a _END_ de _TOTAL_',
        infoEmpty: 'Nenhum registro',
        infoFiltered: '(filtrado de _MAX_)',
        zeroRecords: 'Nada encontrado',
        emptyTable: 'Nenhum registro',
        loadingRecords: 'Carregando...',
        paginate: { first: '«', last: '»', next: '›', previous: '‹' }
    };

    const formatoData = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'medium' });

    // Escapa para HTML, inclusive dentro de atributos (aspas): o texto pode vir do Sólides DP ou do RHSenso.
    function esc(texto) {
        return texto == null ? '' : String(texto)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function data(iso) {
        return iso ? formatoData.format(new Date(iso)) : '';
    }

    // Datas "de calendário" do RHSenso (sem hora): só o dia, sem conversão de fuso.
    function dia(iso) {
        if (!iso) return '';
        const [a, m, d] = String(iso).substring(0, 10).split('-');
        return `${d}/${m}/${a}`;
    }

    function duracao(texto) {
        // TimeSpan serializado como "hh:mm:ss.fffffff" (ou "d.hh:mm:ss").
        if (!texto) return '';
        const partes = String(texto).split(':');
        if (partes.length < 3) return esc(texto);
        const horas = parseInt(partes[0].split('.').pop(), 10);
        const minutos = parseInt(partes[1], 10);
        const segundos = Math.round(parseFloat(partes[2]));
        if (horas > 0) return `${horas}h${String(minutos).padStart(2, '0')}min`;
        if (minutos > 0) return `${minutos}min${String(segundos).padStart(2, '0')}s`;
        return `${segundos}s`;
    }

    function badge(status) {
        const r = rotulos.status[status] || { texto: status || '—', cor: 'secondary' };
        return `<span class="badge text-bg-${r.cor}">${esc(r.texto)}</span>`;
    }

    function resumo(json, erro) {
        if (erro) return `<span class="text-danger small mensagem">${esc(erro.length > 160 ? erro.substring(0, 160) + '…' : erro)}</span>`;
        if (!json) return '';
        try {
            const contagens = JSON.parse(json);
            return Object.keys(contagens).map(entidade => {
                const partes = Object.keys(contagens[entidade])
                    .filter(s => s !== 'unchanged')
                    .map(s => `${badge(s)} ${contagens[entidade][s]}`);
                return partes.length ? `<div class="small"><strong>${esc(rotulos.entidades[entidade] || entidade)}:</strong> ${partes.join(' ')}</div>` : '';
            }).join('') || '<span class="small text-body-secondary">sem alterações</span>';
        } catch {
            return '';
        }
    }

    function linkHistorico(tipo, externalId) {
        if (!externalId) return '';
        // Itens de férias usam "{colaborador}:{feria2.id}": o histórico é o do colaborador.
        const chave = String(externalId).split(':')[0];
        const destino = tipo === 'vacation' ? 'employee' : tipo;
        const url = `${window.appBase || '/'}Migrados/Historico?tipo=${encodeURIComponent(destino)}&id=${encodeURIComponent(chave)}`;
        return `<a href="${url}"><code>${esc(externalId)}</code></a>`;
    }

    // ---------------------------------------------------------------- caixa de diálogo padrão
    // Modal do Bootstrap (#app-dialogo, no _Layout) no lugar de alert/confirm/prompt do navegador.
    // Os textos entram com textContent: nunca interpretam HTML.
    const estilosDialogo = {
        info: { icone: 'bi-info-circle-fill', cor: 'text-primary', botao: 'btn-primary' },
        aviso: { icone: 'bi-exclamation-triangle-fill', cor: 'text-warning', botao: 'btn-warning' },
        perigo: { icone: 'bi-exclamation-octagon-fill', cor: 'text-danger', botao: 'btn-danger' },
        sucesso: { icone: 'bi-check-circle-fill', cor: 'text-success', botao: 'btn-success' }
    };

    /**
     * Abre a caixa de diálogo. Opções: titulo, mensagem, tipo (info|aviso|perigo|sucesso), botao (texto do botão
     * de confirmar), campo (rótulo de um texto obrigatório, ex.: "Motivo"), somenteOk (só o botão OK).
     * Resolve com true/false; com campo, com o texto digitado ou null se cancelou.
     */
    function dialogo(opcoes) {
        const el = document.getElementById('app-dialogo');
        if (!el || !window.bootstrap) {
            // Página sem o modal (ex.: tela de login): diálogo do navegador.
            if (opcoes.campo) {
                const texto = window.prompt(opcoes.mensagem);
                return Promise.resolve(texto && texto.trim() ? texto.trim() : null);
            }
            if (opcoes.somenteOk) {
                window.alert(opcoes.mensagem);
                return Promise.resolve(true);
            }
            return Promise.resolve(window.confirm(opcoes.mensagem));
        }

        if (el.classList.contains('show')) {
            // Já há uma caixa aberta (ex.: vários erros de grade de uma vez): não empilha.
            return Promise.resolve(opcoes.campo ? null : false);
        }

        const estilo = estilosDialogo[opcoes.tipo] || estilosDialogo.info;
        const q = seletor => el.querySelector(seletor);
        const texto = q('#app-dialogo-texto');
        const ok = q('[data-dialogo-ok]');

        q('[data-dialogo-icone]').className = `bi ${estilo.icone} ${estilo.cor}`;
        q('[data-dialogo-titulo]').textContent = opcoes.titulo || 'Confirmação';
        q('[data-dialogo-mensagem]').textContent = opcoes.mensagem || '';
        q('[data-dialogo-campo]').classList.toggle('d-none', !opcoes.campo);
        q('[data-dialogo-rotulo]').textContent = opcoes.campo || '';
        q('[data-dialogo-cancelar]').classList.toggle('d-none', !!opcoes.somenteOk);
        texto.value = '';
        texto.classList.remove('is-invalid');
        ok.className = `btn ${estilo.botao}`;
        ok.textContent = opcoes.botao || (opcoes.somenteOk ? 'OK' : 'Confirmar');

        const modal = bootstrap.Modal.getOrCreateInstance(el);
        return new Promise(resolve => {
            let resultado = opcoes.campo ? null : false;
            ok.onclick = () => {
                if (opcoes.campo) {
                    const valor = texto.value.trim();
                    if (!valor) {
                        texto.classList.add('is-invalid');
                        texto.focus();
                        return;
                    }
                    resultado = valor;
                } else {
                    resultado = true;
                }
                modal.hide();
            };
            el.addEventListener('shown.bs.modal', () => (opcoes.campo ? texto : ok).focus(), { once: true });
            el.addEventListener('hidden.bs.modal', () => {
                ok.onclick = null;
                resolve(resultado);
            }, { once: true });
            modal.show();
        });
    }

    const confirmar = (mensagem, opcoes) => dialogo(Object.assign({ mensagem }, opcoes));
    const pedirMotivo = (mensagem, opcoes) => dialogo(Object.assign({ mensagem, campo: 'Motivo', tipo: 'aviso' }, opcoes));
    const avisar = (mensagem, opcoes) => dialogo(Object.assign({ mensagem, somenteOk: true }, opcoes));

    // Erro numa grade (ex.: sessão expirada, servidor fora): caixa padrão em vez do alert do DataTables.
    if (window.DataTable) {
        DataTable.ext.errMode = (settings, tecnico, mensagem) => avisar(
            'Não foi possível carregar os dados desta lista. Recarregue a página (F5); se continuar, avise o TI.\n\nDetalhe técnico: ' + mensagem,
            { titulo: 'Erro ao carregar a lista', tipo: 'perigo' });
    }

    function grid(seletor, url, colunas, opcoes) {
        opcoes = opcoes || {};
        return new DataTable(seletor, Object.assign({
            serverSide: true,
            processing: true,
            searchDelay: 400,
            pageLength: 25,
            order: opcoes.order || [[0, 'desc']],
            language: idioma,
            columns: colunas,
            ajax: {
                url: url,
                // Só o que o servidor lê (DataTables.cs). O padrão do DataTables manda ~6 parâmetros por coluna
                // (columns[i][data], [searchable]...) e a URL passa do limite de query string do IIS (2048 bytes,
                // erro 404.15), que não existe no Kestrel do ambiente de desenvolvimento.
                data: d => {
                    const ordem = d.order && d.order.length ? d.order[0] : { column: 0, dir: 'desc' };
                    return Object.assign({
                        draw: d.draw,
                        start: d.start,
                        length: d.length,
                        'search[value]': d.search ? d.search.value : '',
                        'order[0][column]': ordem.column,
                        'order[0][dir]': ordem.dir
                    }, opcoes.params ? opcoes.params() : {});
                }
            }
        }, opcoes.dt || {}));
    }

    window.app = {
        esc, data, dia, duracao, badge, resumo, linkHistorico, grid, dialogo, confirmar, pedirMotivo, avisar,
        // Textos do DataTables em português, para grades que não usam app.grid (ex.: dados já na página).
        idioma,
        entidade: t => esc(rotulos.entidades[t] || t),
        acao: a => esc(rotulos.acoes[a] || a),
        comando: c => esc(rotulos.comandos[c] || c),
        origem: o => esc(rotulos.origens[o] || o)
    };

    // Menu lateral: no celular abre por cima do conteúdo; no computador recolhe e o conteúdo usa a tela toda
    // (a escolha fica guardada neste navegador).
    document.addEventListener('click', e => {
        if (!e.target.closest('[data-alternar-menu]')) {
            return;
        }

        if (window.matchMedia('(min-width: 992px)').matches) {
            const recolhido = document.body.classList.toggle('menu-recolhido');
            try { localStorage.setItem('menu-recolhido', recolhido ? '1' : '0'); } catch (erro) { /* navegador sem armazenamento */ }
        } else {
            document.querySelector('.app-sidebar')?.classList.toggle('aberta');
        }
    });

    // Formulários que pedem confirmação, com a caixa padrão:
    //   <form data-confirmar="texto">            confirmação
    //   <form data-motivo="texto">               motivo obrigatório, gravado no <input name="motivo">
    //   data-dialogo-titulo / data-dialogo-tipo (info|aviso|perigo|sucesso) / data-dialogo-botao: opcionais.
    // O botão de confirmar repete o texto do botão clicado ("Desativar", "Executar agora").
    document.addEventListener('submit', async e => {
        const form = e.target;
        if (form.dataset.dialogoConfirmado === '1') {
            delete form.dataset.dialogoConfirmado;
            return;
        }

        const pedeMotivo = form.dataset.motivo !== undefined;
        if (!pedeMotivo && !form.dataset.confirmar) {
            return;
        }

        e.preventDefault();
        const botao = e.submitter;
        const opcoes = {
            titulo: form.dataset.dialogoTitulo,
            tipo: form.dataset.dialogoTipo || (pedeMotivo ? 'aviso' : 'info'),
            botao: form.dataset.dialogoBotao || (botao && botao.textContent.trim()) || undefined
        };

        if (pedeMotivo) {
            const motivo = await pedirMotivo(form.dataset.motivo, Object.assign({}, opcoes, { titulo: opcoes.titulo || 'Informe o motivo' }));
            if (motivo === null) return;
            form.querySelector('input[name="motivo"]').value = motivo;
        } else if (!await confirmar(form.dataset.confirmar, Object.assign({}, opcoes, { titulo: opcoes.titulo || 'Confirmação' }))) {
            return;
        }

        form.dataset.dialogoConfirmado = '1';
        if (form.requestSubmit) {
            form.requestSubmit(botao && botao.form === form ? botao : undefined);
        } else {
            form.submit();
        }
    });

    // Formulário POST enviado (já confirmado): o botão fica desabilitado, com spinner, até a próxima tela.
    // Evita o clique duplo e mostra que algo está acontecendo. <form data-sem-espera> desliga.
    document.addEventListener('submit', e => {
        const form = e.target;
        if (e.defaultPrevented || (form.method || '').toLowerCase() !== 'post' || form.dataset.semEspera !== undefined) {
            return;
        }

        const botao = e.submitter && e.submitter.form === form ? e.submitter : form.querySelector('button[type="submit"]');
        if (!botao || botao.dataset.ocupado !== undefined) {
            return;
        }

        botao.dataset.ocupado = '';
        // Desabilitar já tiraria o nome/valor do botão do envio: só depois que o navegador montou o POST.
        setTimeout(() => {
            botao.disabled = true;
            botao.insertAdjacentHTML('afterbegin', '<span class="spinner-border spinner-border-sm me-1" data-spinner-envio aria-hidden="true"></span>');
        }, 0);
    });

    // Voltar pelo navegador (cache da página): os botões voltam ao normal.
    window.addEventListener('pageshow', ev => {
        if (!ev.persisted) return;
        document.querySelectorAll('.btn[data-ocupado]').forEach(b => {
            b.disabled = false;
            delete b.dataset.ocupado;
            b.querySelector('[data-spinner-envio]')?.remove();
        });
    });

    // Pedido ao serviço em andamento (Comandos/Detalhe): consulta a situação a cada 2 s e, quando termina, recarrega a
    // tela com o resultado (o spinner some).
    function acompanharPedido(painel) {
        const url = painel.dataset.situacaoUrl;
        const inicio = new Date(Number(painel.dataset.solicitadoEm));
        const q = seletor => painel.querySelector(seletor);
        const tempo = q('[data-processando-tempo]');
        const etapa = nome => q(`[data-etapa="${nome}"]`);
        let status = painel.dataset.status;
        const naFila = painel.dataset.statusFila;
        const executando = painel.dataset.statusExecutando;
        let falhasSeguidas = 0;

        const formatarTempo = ms => {
            const total = Math.max(0, Math.round(ms / 1000));
            const min = Math.floor(total / 60);
            return min > 0 ? `${min}min ${String(total % 60).padStart(2, '0')}s` : `${total}s`;
        };
        const relogio = setInterval(() => {
            const decorrido = Date.now() - inicio.getTime();
            tempo.textContent = formatarTempo(decorrido);
            // Mais de 1 minuto na fila: provavelmente o serviço está parado.
            q('[data-processando-aviso]').classList.toggle('d-none', !(status === naFila && decorrido > 60000));
        }, 1000);

        const marcar = (el, classe, icone) => {
            if (!el) return;
            el.classList.remove('feita', 'atual');
            if (classe) el.classList.add(classe);
            if (icone) el.querySelector('.bi').className = `bi ${icone}`;
        };

        async function consultar() {
            if (document.hidden) {
                setTimeout(consultar, 2000);
                return;
            }

            try {
                const resposta = await fetch(url, { headers: { Accept: 'application/json' }, cache: 'no-store' });
                if (resposta.status === 404) {
                    // O pedido não existe mais: a tela mostra a situação atual.
                    clearInterval(relogio);
                    window.location.reload();
                    return;
                }
                if (!resposta.ok) throw new Error('HTTP ' + resposta.status);
                const situacao = await resposta.json();
                falhasSeguidas = 0;
                if (situacao.status !== status && situacao.status === executando) {
                    marcar(etapa('fila'), 'feita', 'bi-check-circle-fill');
                    marcar(etapa('executando'), 'atual');
                    q('[data-processando-titulo]').textContent = 'Processando…';
                    q('[data-processando-texto]').textContent = 'O serviço está executando o pedido. Isso pode levar alguns minutos.';
                }

                status = situacao.status;
                if (!situacao.emAberto) {
                    clearInterval(relogio);
                    painel.classList.add('concluido');
                    marcar(etapa('fila'), 'feita', 'bi-check-circle-fill');
                    marcar(etapa('executando'), 'feita', 'bi-check-circle-fill');
                    marcar(etapa('concluido'), 'feita', 'bi-check-circle-fill');
                    q('.processando-spinner').classList.add('d-none');
                    q('[data-processando-titulo]').textContent = 'Concluído. Carregando o resultado…';
                    setTimeout(() => window.location.reload(), 600);
                    return;
                }
            } catch (erro) {
                // Rede ou sessão: tenta de novo, mais devagar.
                falhasSeguidas++;
            }

            setTimeout(consultar, falhasSeguidas > 0 ? Math.min(15000, 2000 * (falhasSeguidas + 1)) : 2000);
        }

        tempo.textContent = formatarTempo(Date.now() - inicio.getTime());
        setTimeout(consultar, 2000);
    }

    document.addEventListener('DOMContentLoaded', () => {
        const painel = document.getElementById('processando');
        if (painel && painel.dataset.situacaoUrl) {
            acompanharPedido(painel);
        }
    });
})();

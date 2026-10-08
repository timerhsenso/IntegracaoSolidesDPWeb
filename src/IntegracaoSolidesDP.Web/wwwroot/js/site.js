// Funções comuns das telas: grades (DataTables server-side), rótulos e confirmações.
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

    function esc(texto) {
        const div = document.createElement('div');
        div.textContent = texto == null ? '' : String(texto);
        return div.innerHTML;
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
                data: d => Object.assign(d, opcoes.params ? opcoes.params() : {})
            }
        }, opcoes.dt || {}));
    }

    window.app = {
        esc, data, dia, duracao, badge, resumo, linkHistorico, grid,
        entidade: t => esc(rotulos.entidades[t] || t),
        acao: a => esc(rotulos.acoes[a] || a),
        comando: c => esc(rotulos.comandos[c] || c),
        origem: o => esc(rotulos.origens[o] || o)
    };

    // Menu lateral no celular.
    document.addEventListener('click', e => {
        if (e.target.closest('[data-alternar-menu]')) {
            document.querySelector('.app-sidebar')?.classList.toggle('aberta');
        }
    });

    // <form data-motivo="pergunta"> pede o motivo; <form data-confirmar="pergunta"> pede confirmação.
    document.addEventListener('submit', e => {
        const form = e.target;
        if (form.dataset.motivo !== undefined) {
            const motivo = window.prompt(form.dataset.motivo);
            if (!motivo || !motivo.trim()) {
                e.preventDefault();
                return;
            }
            form.querySelector('input[name="motivo"]').value = motivo.trim();
        } else if (form.dataset.confirmar && !window.confirm(form.dataset.confirmar)) {
            e.preventDefault();
        }
    });
})();

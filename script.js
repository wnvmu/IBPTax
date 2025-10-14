document.addEventListener("DOMContentLoaded", function() {
    // Carregar a versão dinamicamente do arquivo versao.json local
    fetch(`https://wnvmu.github.io/IBPTax/versao.json`)
        .then(response => response.json())
        .then(data => {
            const versionSelect = document.getElementById('version');
            const version = data.VERSAO; // Obter a versão do arquivo versao.json
            const option = document.createElement('option');
            option.value = version;
            option.textContent = version;
            versionSelect.appendChild(option);
        })
        .catch(error => {
            console.error('Erro ao carregar a versão:', error);
            alert('Erro ao carregar a versão.');
        });
});

document.getElementById('searchButton').addEventListener('click', function() {
    const ncmCode = document.getElementById('ncm').value.trim();
    const selectedVersion = document.getElementById('version').value;
    const selectedState = document.getElementById('state').value;

    if (ncmCode) {
        fetchNCMData(selectedVersion, selectedState, ncmCode);
    } else {
        alert('Por favor, insira um código NCM.');
    }
});

// Função para buscar os dados do NCM
function fetchNCMData(version, state, ncmCode) {
    const url = `https://wnvmu.github.io/IBPTax/${version}/${state}/NCM/${ncmCode}.json`;

    fetch(url)
        .then(response => response.json())
        .then(data => {
            displayNCMData(data);
        })
        .catch(error => {
            console.error('Erro ao obter dados do NCM:', error);
            document.getElementById('ncmDetails').innerHTML = '<p><strong>Erro:</strong> NCM não encontrado ou dados não disponíveis.</p>';
        });
}

// Função para exibir os dados do NCM na tela
function displayNCMData(data) {
    const ncmDetails = document.getElementById('ncmDetails');
    ncmDetails.innerHTML = ''; // Limpa resultados anteriores

    const renderItem = (item) => {
        return `
				<div class="ncm-item">
				<div class="ncm-header">
				<h3>Código NCM: ${item.CODIGO || 'Não disponível'}</h3>
				<p><strong>Descrição:</strong> ${item.DESCRICAO || 'Não disponível'}</p>
				</div>

				<div class="ncm-grid">
				<div><strong>Exceção Fiscal:</strong><BR> ${item.EXCECAO_FISCAL || 'Não disponível'}</BR></div>
				<div><strong>Fonte:</strong><BR> ${item.FONTE || 'Não disponível'}</BR></div>
				<div><strong>Alíquota Federal Nacional:</strong><BR> ${item.ALIQ_FED_NAC ?? 'Não disponível'}% </BR></div>
				<div><strong>Alíquota Federal Importação:</strong><BR> ${item.ALIQ_FED_IMP ?? 'Não disponível'}%</BR></div>
				<div><strong>Alíquota Estadual:</strong><BR> ${item.ALIQ_ESTADUAL ?? 'Não disponível'}%</BR></div>
				<div><strong>Alíquota Municipal:</strong><BR> ${item.ALIQ_MUNICIPAL ?? 'Não disponível'}%</BR></div>
				<div><strong>Data Início Vigência:</strong><BR> ${item.DT_INICIO_VIG || 'Não disponível'}</BR></div>
				<div><strong>Data Fim Vigência:</strong><BR> ${item.DT_FIM_VIG || 'Não disponível'}</BR></div>
				</div>
				</div>

        `;
    };

    if (Array.isArray(data) && data.length > 0) {
        // Se for array, renderiza todos
        data.forEach(item => {
            ncmDetails.innerHTML += renderItem(item);
        });
    } else if (typeof data === 'object' && data !== null && data.CODIGO) {
        // Se for objeto único com CODIGO
        ncmDetails.innerHTML = renderItem(data);
    } else {
        // Nenhum dado útil encontrado
        ncmDetails.innerHTML = '<p><strong>Erro:</strong> Dados não encontrados ou formato inesperado.</p>';
    }
}


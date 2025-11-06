using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;


static class CsvToJson
{
    // Detecta o TIPO a partir do padrão do código (inclui NBS com/sem pontuação)
    private static string DetectTipo(string codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            return "OUT";

        string raw = codigo.Trim().ToUpperInvariant();
        string semPontuacao = raw.Replace(".", "").Replace("-", "").Replace(" ", "");

        // NBS: S + 8 dígitos (com/sem pontuação)
        if (Regex.IsMatch(semPontuacao, @"^S\d{8}$")) return "NBS";
        if (Regex.IsMatch(raw, @"^S\d{4}\.\d{4}$")) return "NBS";
        if (Regex.IsMatch(raw, @"^S\d{2}\.\d{2}\.\d{2}\.\d{2}$")) return "NBS";

        // NCM: 8 dígitos com/sem pontuação visual
        if (Regex.IsMatch(semPontuacao, @"^\d{8}$")) return "NCM";
        if (Regex.IsMatch(raw, @"^\d{4}\.\d{4}$")) return "NCM";

        // LC116
        if (Regex.IsMatch(raw, @"^[0-9]{2,5}(\.[0-9]{1,2})?$")) return "LC116";

        return "OUT";
    }

    // Normaliza versão (elimina pontos duplos e garante sufixo .<LETRA>, default .E)
    public static string NormalizeVersion(string raw)
    {
        // Base neutra: 0.0.E (major.minor.LETRA)
        if (string.IsNullOrWhiteSpace(raw)) return "0.0.E";

        string s = Regex.Replace(raw.Trim().ToUpperInvariant(), @"[^0-9A-Z.]", "");
        s = Regex.Replace(s, @"\.{2,}", ".");
        s = s.Trim('.');

        // Se vier como "25.2F", vira "25.2.F"
        s = Regex.Replace(s, @"(?<=\d)([A-Z])$", ".$1");

        // >>> Mudança p/ C# 7.3: usar char[] no Split
        var parts = s.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        // Garante pelo menos major.minor
        if (parts.Count == 1) parts.Add("0");

        if (parts.Count == 2)
        {
            parts.Add("E");
        }
        else
        {
            // >>> Mudança p/ C# 7.3: sem parts[^1]
            string last = parts[parts.Count - 1];
            if (!Regex.IsMatch(last, @"^[A-Z]$"))
                parts.Add("E");
        }

        // >>> Mudança p/ C# 7.3: usar string como separador
        return string.Join(".", parts);
    }

    // Compara versões normalizadas (ex.: 25.2.E > 25.1.E). Ordena por números; letra como sufixo.
    public static int CompareVersions(string v1, string v2)
    {
        string a = NormalizeVersion(v1);
        string b = NormalizeVersion(v2);

        var pa = a.Split('.');
        var pb = b.Split('.');

        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            // Preenche faltantes: números -> "0"; sufixo final -> "E"
            string ia = i < pa.Length ? pa[i] : (i == len - 1 ? "E" : "0");
            string ib = i < pb.Length ? pb[i] : (i == len - 1 ? "E" : "0");

            bool aNum = int.TryParse(ia, out int nia);
            bool bNum = int.TryParse(ib, out int nib);

            if (aNum && bNum)
            {
                int cmp = nia.CompareTo(nib);
                if (cmp != 0) return cmp;
                continue;
            }

            // Se ambos são apenas uma letra (sufixo), compara A..Z
            if (Regex.IsMatch(ia, @"^[A-Z]$") && Regex.IsMatch(ib, @"^[A-Z]$"))
            {
                int cmp = ia[0].CompareTo(ib[0]);
                if (cmp != 0) return cmp;
                continue;
            }

            // Caso raro: algo fora do padrão (ex.: "RC1"): compara ordinal
            int scmp = string.Compare(ia, ib, StringComparison.Ordinal);
            if (scmp != 0) return scmp;
        }
        return 0;
    }

    private static string CleanText(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if ((s.StartsWith("\"") && s.EndsWith("\"")) || (s.StartsWith("'") && s.EndsWith("'")))
            s = s.Substring(1, s.Length - 2);
        s = s.Replace("\\u0022", "\"");
        return s.Trim();
    }

    private static string KeyEx(string excecao) =>
        string.IsNullOrWhiteSpace(excecao) ? "" : excecao.Trim().ToUpperInvariant();

    private static int CompareByDtInicio(string ymd1, string ymd2)
    {
        if (DateTime.TryParseExact(ymd1 ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1) &&
            DateTime.TryParseExact(ymd2 ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            return d1.CompareTo(d2);
        if (!string.IsNullOrEmpty(ymd1) && string.IsNullOrEmpty(ymd2)) return 1;
        if (string.IsNullOrEmpty(ymd1) && !string.IsNullOrEmpty(ymd2)) return -1;
        return 0;
    }

    private static bool IsTipoValido(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return false;
        t = t.Trim().ToUpperInvariant();
        return t == "NCM" || t == "NBS" || t == "LC116" || t == "OUT";
    }

    private static string NormalizeTipo(string tipoCsv, string codigo)
    {
        var t = (tipoCsv ?? "").Trim().ToUpperInvariant();
        if (IsTipoValido(t)) return t;
        return DetectTipo(codigo);
    }

    private static string ParseDateYMD(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (DateTime.TryParseExact(v, "dd/MM/yyyy", new CultureInfo("pt-BR"), DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd");
        if (DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return dt.ToString("yyyy-MM-dd");
        if (DateTime.TryParse(v, out dt))
            return dt.ToString("yyyy-MM-dd");
        return null;
    }

    private static decimal? ParseDec4(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim().Replace(',', '.');
        if (decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return Math.Round(d, 4);
        return null;
    }

    public class IbptJson
    {
        public string CODIGO { get; set; }
        public string EXCECAO_FISCAL { get; set; }
        public string DESCRICAO { get; set; }
        public decimal ALIQ_FED_NAC { get; set; }
        public decimal ALIQ_FED_IMP { get; set; }
        public decimal ALIQ_ESTADUAL { get; set; }
        public decimal ALIQ_MUNICIPAL { get; set; }
        public string DT_INICIO_VIG { get; set; }
        public string DT_FIM_VIG { get; set; }
        public string FONTE { get; set; }
    }

    // Lê CSV e grava JSONs na estrutura: outDir/versão/UF/TIPO/CODIGO.json
    public static int ProcessFileToJson(string csvPath, string outDir, StreamWriter log)
    {
        try
        {
            Match m = Regex.Match(Path.GetFileNameWithoutExtension(csvPath),
                @"IBPTax([A-Z]{2})([0-9]+(?:\.[0-9]+)*)(?:\.?([A-Z]))?", RegexOptions.IgnoreCase);

            string UF = m.Success ? m.Groups[1].Value.ToUpperInvariant() : "XX";
            string core = m.Success ? m.Groups[2].Value : "";
            string suf = (m.Success && m.Groups[3].Success) ? m.Groups[3].Value.ToUpperInvariant() : null;
            string versaoFromName = string.IsNullOrEmpty(core) ? "versao" : (suf == null ? core : core + "." + suf);
            string versaoNorm = NormalizeVersion(versaoFromName);

            // Atualiza versao.json dinâmico na raiz de outDir (IBPTAX)
            try
            {
                string date = DateTime.Now.ToString("yyyy-MM-dd");
                string time = DateTime.Now.ToString("HH:mm:ss");
                var meta = new { VERSAO = versaoNorm, DATA = date, HORA = time };
                var metaOpts = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                File.WriteAllText(Path.Combine(outDir, "versao.json"), JsonSerializer.Serialize(meta, metaOpts), Encoding.UTF8);
            }
            catch (Exception exMeta)
            {
                log.WriteLine($"[WARN] Falha ao gravar versao.json: {exMeta.Message}");
            }

            Encoding enc = Encoding.GetEncoding("latin1");
            string[] lines = File.ReadAllLines(csvPath, enc);
            if (lines.Length == 0)
            {
                log.WriteLine($"[AVISO] Arquivo vazio: {Path.GetFileName(csvPath)}");
                return 0;
            }

            string[] header = lines[0].Split(';');
            int idx(string name)
            {
                for (int i = 0; i < header.Length; i++)
                    if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                        return i;
                return -1;
            }

            int iCodigo = idx("codigo"),
                iEx = idx("ex"),
                iTipo = idx("tipo"),
                iDescricao = idx("descricao"),
                iNacFed = idx("nacionalfederal"),
                iImpFed = idx("importadosfederal"),
                iEst = idx("estadual"),
                iMun = idx("municipal"),
                iIni = idx("vigenciainicio"),
                iFim = idx("vigenciafim"),
                iFonte = idx("fonte");

            string GetCol(string[] cols, int index) => (index >= 0 && index < cols.Length) ? cols[index] : "";

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            int count = 0;
            int total = lines.Length - 1;
            Stopwatch sw = Stopwatch.StartNew();

            for (int ln = 1; ln < lines.Length; ln++)
            {
                if (string.IsNullOrWhiteSpace(lines[ln])) continue;
                string[] cols = lines[ln].Split(';');

                string codigo = GetCol(cols, iCodigo).Trim();
                if (string.IsNullOrEmpty(codigo)) continue;

                string tipoCsv = GetCol(cols, iTipo);
                string tipo = NormalizeTipo(tipoCsv, codigo);

                if (!(tipo == "NCM" || tipo == "NBS" || tipo == "LC116"))
                    continue;

                string ex = GetCol(cols, iEx)?.Trim();
                string descricao = GetCol(cols, iDescricao);
                var aliqNac = ParseDec4(GetCol(cols, iNacFed));
                var aliqImp = ParseDec4(GetCol(cols, iImpFed));
                var aliqEst = ParseDec4(GetCol(cols, iEst));
                var aliqMun = ParseDec4(GetCol(cols, iMun));
                var dtIni = ParseDateYMD(GetCol(cols, iIni));
                var dtFim = ParseDateYMD(GetCol(cols, iFim));
                var fonte = GetCol(cols, iFonte);

                if (aliqNac == null || aliqImp == null || aliqEst == null || aliqMun == null || dtIni == null)
                    continue;

                var item = new IbptJson
                {
                    CODIGO = codigo,
                    EXCECAO_FISCAL = CleanText(ex),
                    DESCRICAO = CleanText(descricao),
                    ALIQ_FED_NAC = aliqNac.Value,
                    ALIQ_FED_IMP = aliqImp.Value,
                    ALIQ_ESTADUAL = aliqEst.Value,
                    ALIQ_MUNICIPAL = aliqMun.Value,
                    DT_INICIO_VIG = dtIni,
                    DT_FIM_VIG = dtFim,
                    FONTE = CleanText(fonte)
                };

                string dir = Path.Combine(outDir, versaoNorm, UF, tipo);
                Directory.CreateDirectory(dir);

                string filePath = Path.Combine(dir, $"{codigo}.json");

                // Carrega existente como lista
                List<IbptJson> lista;
                if (File.Exists(filePath))
                {
                    try
                    {
                        string existing = File.ReadAllText(filePath, Encoding.UTF8).Trim();
                        if (existing.StartsWith("["))
                            lista = JsonSerializer.Deserialize<List<IbptJson>>(existing) ?? new List<IbptJson>();
                        else if (existing.StartsWith("{"))
                        {
                            var single = JsonSerializer.Deserialize<IbptJson>(existing);
                            lista = new List<IbptJson>();
                            if (single != null) lista.Add(single);
                        }
                        else lista = new List<IbptJson>();
                    }
                    catch (Exception exRead)
                    {
                        lista = new List<IbptJson>();
                        log.WriteLine($"[WARN] Falha ao ler {filePath}: {exRead.Message}. Recriando o arquivo.");
                    }
                }
                else
                {
                    lista = new List<IbptJson>();
                }

                // Agrega por EXCECAO_FISCAL mantendo a mais recente (DT_INICIO_VIG)
                string novaKey = KeyEx(item.EXCECAO_FISCAL);
                int foundIndex = -1;
                for (int i = 0; i < lista.Count; i++)
                    if (KeyEx(lista[i].EXCECAO_FISCAL) == novaKey) { foundIndex = i; break; }

                if (foundIndex >= 0)
                {
                    var existente = lista[foundIndex];
                    if (CompareByDtInicio(item.DT_INICIO_VIG, existente.DT_INICIO_VIG) > 0)
                        lista[foundIndex] = item;
                }
                else
                {
                    lista.Add(item);
                }

                string outJson = (lista.Count == 1)
                    ? JsonSerializer.Serialize(lista[0], jsonOptions)
                    : JsonSerializer.Serialize(lista, jsonOptions);

                outJson = outJson.Replace("\\u0022", "\"");
                File.WriteAllText(filePath, outJson, Encoding.UTF8);

                count++;

                if (count % 200 == 0 || ln == total)
                {
                    double progress = (double)ln / total * 100;
                    double elapsedSec = sw.Elapsed.TotalSeconds;
                    double estTotal = progress > 0 ? elapsedSec / (progress / 100) : 0;
                    double remaining = estTotal - elapsedSec;
                    Console.CursorLeft = 0;
                    Console.Write($"Progresso: {progress:0.0}% | Arquivos atualizados: {count} | ETA: {Math.Max(0, remaining):0.0}s   ");
                }
            }

            sw.Stop();
            log.WriteLine($"[OK] {Path.GetFileName(csvPath)} -> {count} arquivo(s) de código atualizados. Tempo: {sw.Elapsed.TotalSeconds:0.0}s");
            return count;
        }
        catch (Exception ex)
        {
            log.WriteLine($"[ERRO] {Path.GetFileName(csvPath)}: {ex.Message}");
            return 0;
        }
    }
}

class Program
{
    private const string GitHubApiUrl = "https://api.github.com/repos/ProjetoACBr/ACBr/contents/Exemplos/ACBrTCP/ACBrIBPTax/tabela";

    // Lê VERSAO local do arquivo IBPTAX\versao.json, se existir
    private static string ReadLocalVersion(string outDir)
    {
        string p = Path.Combine(outDir, "versao.json");
        if (!File.Exists(p)) return null;
        try
        {
            var fs = File.OpenRead(p);
            var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.TryGetProperty("VERSAO", out var v))
                return v.GetString();
        }
        catch { }
        return null;
    }

    // Compara e, se remoto > local, baixa CSVs da versão remota mais nova
    private static List<string> SyncFromGitHub(string outDir, StreamWriter log, out string remoteLatestVersion)
    {
        remoteLatestVersion = null;
        var downloadedCsvs = new List<string>();

        try
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("IBPTaxSync/1.0 (+https://github.com/)");

            // 1) Lista conteúdo da pasta
            string json = http.GetStringAsync(GitHubApiUrl).Result;

            // 2) Parse da lista (array de objetos com 'name' e 'download_url')
            var doc = JsonDocument.Parse(json);
            var entries = new List<(string name, string downloadUrl)>();

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("name", out var n) || !el.TryGetProperty("download_url", out var d))
                    continue;
                string name = n.GetString();
                string dl = d.GetString();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dl)) continue;
                if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    entries.Add((name, dl));
            }

            if (entries.Count == 0)
            {
                log.WriteLine("[WARN] Nenhum CSV encontrado no repositório remoto.");
                return downloadedCsvs;
            }

            // 3) Descobre versões pelos nomes (IBPTaxUF<versao>.csv)
            string ExtractVersion(string fileName)
            {
                var name = Path.GetFileNameWithoutExtension(fileName);

                // IBPTax + país (2 letras) + separador opcional + versão (n.n.n...) + sufixo-letra opcional (.F ou F)
                var rx = @"IBPTax([A-Z]{2})[ _\-]*([0-9]+(?:\.[0-9]+)*)(?:\.?([A-Z]))?";
                var m = Regex.Match(name, rx, RegexOptions.IgnoreCase);

                if (!m.Success) return null;

                var core = m.Groups[2].Value; // ex.: "25.2"
                var suf = m.Groups[3].Success ? m.Groups[3].Value.ToUpperInvariant() : null;

                var version = string.IsNullOrEmpty(suf) ? core : (core + "." + suf); // "25.2.F"
                return CsvToJson.NormalizeVersion(version); // garante "major.minor.LETRA"
            }

            // Agrupa por versão normalizada
            var versionBuckets = new Dictionary<string, List<(string name, string url)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                string v = ExtractVersion(e.name);
                if (string.IsNullOrEmpty(v)) continue;
                if (!versionBuckets.TryGetValue(v, out var list))
                    versionBuckets[v] = list = new List<(string, string)>();
                list.Add((e.name, e.downloadUrl));
            }

            if (versionBuckets.Count == 0)
            {
                log.WriteLine("[WARN] Não foi possível identificar versões nos nomes dos arquivos remotos.");
                return downloadedCsvs;
            }

            // 4) Pega a MAIOR versão remota
            string maxRemote = null;
            foreach (var v in versionBuckets.Keys)
                if (maxRemote == null || CsvToJson.CompareVersions(v, maxRemote) > 0)
                    maxRemote = v;

            remoteLatestVersion = maxRemote;

            string localVersion = ReadLocalVersion(outDir);
            if (localVersion != null && CsvToJson.CompareVersions(maxRemote, localVersion) <= 0)
            {
                log.WriteLine($"[SYNC] Versão remota {maxRemote} <= versão local {localVersion}. Nada a sincronizar.");
                return downloadedCsvs; // vazio → nada a fazer
            }

            log.WriteLine($"[SYNC] Nova versão disponível: {maxRemote}. Baixando CSVs...");

            // 5) Baixa todos os CSVs dessa versão para uma pasta cache
            string cacheDir = Path.Combine(outDir, "_cache", maxRemote);
            Directory.CreateDirectory(cacheDir);

            foreach (var (name, url) in versionBuckets[maxRemote])
            {
                string dest = Path.Combine(cacheDir, name);
                try
                {
                    var http2 = new HttpClient();
                    http2.DefaultRequestHeaders.UserAgent.ParseAdd("IBPTaxSync/1.0 (+https://github.com/)");
                    var bytes = http2.GetByteArrayAsync(url).Result;
                    File.WriteAllBytes(dest, bytes);
                    downloadedCsvs.Add(dest);
                }
                catch (Exception exDl)
                {
                    log.WriteLine($"[WARN] Falha ao baixar {name}: {exDl.Message}");
                }
            }

            if (downloadedCsvs.Count == 0)
                log.WriteLine("[WARN] Nenhum CSV foi baixado para a nova versão.");

            return downloadedCsvs;
        }
        catch (Exception ex)
        {
            log.WriteLine($"[ERRO] Falha na sincronização com o GitHub: {ex.Message}");
            return downloadedCsvs;
        }
    }

    static void Main(string[] args)
    {
        Console.Title = "Conversor IBPTax CSV → JSON";
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==============================================");
        Console.WriteLine("   Sistema desenvolvido por Wellington N. Verzola");
        Console.WriteLine("   Analista de desenvolvimento de sistemas");
        Console.WriteLine("==============================================");
        Console.ResetColor();
        Console.WriteLine();

        string appDir = AppDomain.CurrentDomain.BaseDirectory;

        // Base de saída: pasta IBPTAX na raiz do app
        string outDir = Path.GetFullPath(Path.Combine(appDir, ".."));
        Directory.CreateDirectory(outDir);

        string logPath = Path.Combine(outDir, "process.log"); // log dentro de IBPTAX

        using (StreamWriter log = new StreamWriter(logPath, true, Encoding.UTF8))
        {
            log.WriteLine("=== Execução iniciada em " + DateTime.Now + " ===");

            // 1) SINCRONIZA COM O GITHUB (só baixa/processa se versão remota for mais nova)
            string remoteLatest;
            var newCsvs = SyncFromGitHub(outDir, log, out remoteLatest);

            int totalArquivos = 0, totalJsons = 0;

            if (newCsvs.Count > 0)
            {
                Console.WriteLine($"Nova versão remota detectada ({remoteLatest}). Processando arquivos baixados...");
                foreach (var csv in newCsvs)
                {
                    Console.WriteLine("\nProcessando (remoto): " + Path.GetFileName(csv));
                    int gerados = CsvToJson.ProcessFileToJson(csv, outDir, log);
                    totalJsons += gerados;
                    totalArquivos++;
                }
            }
            else
            {
                // 2) Se não houve versão nova, permite processamento manual de CSVs locais
                string[] csvs = Directory.GetFiles(appDir, "*.csv", SearchOption.TopDirectoryOnly);
                if (csvs.Length == 0)
                {
                    Console.WriteLine("Nenhum arquivo .csv encontrado na pasta do aplicativo e nenhuma versão remota nova.");
                    log.WriteLine("Nenhum arquivo CSV local e nenhuma atualização remota.");
                    log.WriteLine("=== Execução encerrada (sem alterações) ===\n");
                    Console.WriteLine("\n🗂️ Log salvo em: process.log");
                    Console.WriteLine("Pressione qualquer tecla para sair...");
                    Console.ReadKey();
                    return;
                }

                Console.WriteLine("Arquivos CSV locais encontrados:");
                for (int i = 0; i < csvs.Length; i++)
                    Console.WriteLine("[" + i + "] " + Path.GetFileName(csvs[i]));

                Console.WriteLine("\nDigite o índice do arquivo para processar ou 'A' para processar TODOS:");
                Console.Write("> ");
                string input = Console.ReadLine()?.Trim() ?? "";

                if (string.Equals(input, "A", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string csv in csvs)
                    {
                        Console.WriteLine("\nProcessando (local): " + Path.GetFileName(csv));
                        int gerados = CsvToJson.ProcessFileToJson(csv, outDir, log);
                        totalJsons += gerados;
                        totalArquivos++;
                    }
                }
                else
                {
                    if (int.TryParse(input, out int idx) && idx >= 0 && idx < csvs.Length)
                    {
                        string csv = csvs[idx];
                        Console.WriteLine("\nProcessando (local): " + Path.GetFileName(csv));
                        int gerados = CsvToJson.ProcessFileToJson(csv, outDir, log);
                        totalJsons += gerados;
                        totalArquivos++;
                    }
                    else
                    {
                        Console.WriteLine("Entrada inválida.");
                        log.WriteLine("Entrada inválida do usuário.");
                    }
                }
            }

            Console.WriteLine($"\n✅ Concluído! CSVs processados: {totalArquivos}, arquivos de código atualizados: {totalJsons}.");
            log.WriteLine($"Finalizado. CSVs processados: {totalArquivos}. Arquivos de código atualizados: {totalJsons}.");
            log.WriteLine("=== Execução encerrada em " + DateTime.Now + " ===\n");
        }

        Console.WriteLine("\n🗂️ Log salvo em: process.log");
        Console.WriteLine("Pressione qualquer tecla para sair...");
        Console.ReadKey();
    }
}

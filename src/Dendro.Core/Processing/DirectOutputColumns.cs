using System.Text.Json.Nodes;

namespace Dendro.Core.Processing;

public static class DirectOutputColumns
{
    public static string[] Headers(string level)
    {
        string[] context=["Parcela_ID","Contexto_ID","Base_Origem","Tipo_Inventario"];
        if(level=="arvore")return ["Talhão","Parcela","Lote","Ano_Medicao","Linha_Origem","DAP","H","AS","Idade_anos","Volume_m3",RegistryAgeFallback.FlagColumn,RegistryAgeFallback.DateColumn,..context];
        if(level=="parcela")return ["Grupo","Talhão","Parcela","Lote","Ano_Medicao","N_Parcelas","DAP_medio","Dq","H_medio","DAPd","Hd","AB","Volume_m3_ha","Fustes_ha","VMI_m3_fuste","Area_efetiva_ha","Volume_total_m3","Fustes_totais_estimados","Fustes_sem_volume","Idade_anos","Idade_meses","Data_Medicao","area_corr",..context];
        return ["Grupo","Talhão","Parcela","Lote","Ano_Medicao","N_Parcelas","Volume_amostrado_m3","Fustes_amostrados","VMI_amostrado_m3_fuste","Volume_m3_ha","Fustes_ha","VMI_m3_fuste","DAPd","Hd","Area_efetiva_ha","Volume_total_m3","Fustes_totais_estimados","Fustes_sem_volume",..(level=="talhao"?new[]{"Idade_anos","Idade_meses"}:Array.Empty<string>()),"Data_Medicao",..context];
    }
    public static bool Required(string level,string column)=>column is "Contexto_ID" or "Base_Origem" or "Tipo_Inventario" or "Ano_Medicao"
        || (level is "arvore" or "parcela") && (column is "Talhão" or "Parcela" or "Lote" or "Parcela_ID" or "Idade_anos")
        || level=="arvore" && column is "Linha_Origem" or "DAP" or "H" or "Volume_m3"
        || level=="parcela" && column is "Data_Medicao" or "area_corr"
        || level!="arvore" && column=="Data_Medicao"
        || level!="arvore" && column is "Volume_m3_ha" or "Fustes_ha" or "VMI_m3_fuste"
        || level=="talhao" && column=="Talhão" || level=="lote" && column=="Lote" || level=="projeto" && column=="Grupo";
    public static bool Enabled(JsonNode? processing,string level,string column)=>Required(level,column)||(processing?["colunas_direto"]?[level].B(column,true)??true);
    public static void Validate(JsonNode? processing)
    {
        if(processing?["colunas_direto"] is not {} settings)return;
        if(settings is not JsonObject levels)throw new InvalidDataException("As colunas do resumo direto devem ser configuradas por nível.");
        foreach(var level in levels)
        {
            if(!DirectInventorySummary.Levels.Contains(level.Key)||level.Value is not JsonObject columns)throw new InvalidDataException("Nível inválido nas colunas do resumo direto.");
            foreach(var column in columns)
            {
                if(!Headers(level.Key).Contains(column.Key)||column.Value is not JsonValue value||!value.TryGetValue<bool>(out bool enabled))throw new InvalidDataException($"Coluna inválida no resumo direto: {level.Key} / {column.Key}.");
                if(!enabled&&Required(level.Key,column.Key))throw new InvalidDataException($"A coluna {column.Key} é obrigatória no nível {level.Key}.");
            }
        }
    }
}

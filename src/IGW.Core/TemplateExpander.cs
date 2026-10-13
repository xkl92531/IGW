using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// 模板展开器(纯函数):把「模板 + 实例差异」展开为该实例的有效 <see cref="DeviceDef"/>。
/// 展开顺序:模板点集 → 应用覆盖(Overrides) → 剔除屏蔽(Masks) → 合入追加(Adds)。
/// 保留模板的分组结构,使展开后点位路径与手工设备一致(<c>通道.设备.组.点</c>);下游(绑定/注册/扫描/北向)对模板无感。
/// 未被覆盖的点按引用共享模板对象(只读消费,零拷贝),仅被覆盖的点生成新对象——面向 2000 实例×500 点的规模。
/// 悬空差异(Path 在模板中不存在)在展开时忽略(校验层给警告)。
/// </summary>
public static class TemplateExpander
{
    /// <summary>在工程内展开设备:模板实例按 <c>TemplateRef</c> 从 <c>proj.Templates</c> 解析并展开;手工设备/模板缺失原样返回。</summary>
    public static DeviceDef ExpandInProject(IgwProject proj, DeviceDef dev)
    {
        if (string.IsNullOrWhiteSpace(dev.TemplateRef)) return dev;
        var tmpl = proj.Templates.FirstOrDefault(t => string.Equals(t.Name, dev.TemplateRef, StringComparison.OrdinalIgnoreCase));
        return tmpl is null ? dev : Expand(tmpl, dev);
    }

    /// <summary>枚举展开后设备的有效点位:(相对路径 组.子组.点, 点名, 地址)。供验证匹配/试读、REST 报告用。</summary>
    public static List<(string Path, string Name, string Address)> EffectiveTags(DeviceDef effective)
    {
        var list = new List<(string, string, string)>();
        foreach (var t in effective.Tags)
            if (!string.IsNullOrWhiteSpace(t.Name)) list.Add((t.Name, t.Name, t.Address));
        void Rec(List<TagGroupDef> gs, string pp)
        {
            foreach (var g in gs)
            {
                string gp = pp.Length == 0 ? g.Name : pp + "." + g.Name;
                foreach (var t in g.Tags)
                    if (!string.IsNullOrWhiteSpace(t.Name)) list.Add((gp + "." + t.Name, t.Name, t.Address));
                Rec(g.SubGroups, gp);
            }
        }
        Rec(effective.Groups, "");
        return list;
    }

    /// <summary>把模板实例展开为有效设备定义。<paramref name="instance"/> 的 Id/Name/Endpoint/Settings 原样保留,点位来自模板+差异。</summary>
    public static DeviceDef Expand(DeviceTemplate template, DeviceDef instance)
    {
        var masks = new HashSet<string>(instance.Delta?.Masks ?? new(), StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, TagOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in instance.Delta?.Overrides ?? new())
            if (!string.IsNullOrWhiteSpace(o.Path)) overrides[o.Path] = o;   // 同 Path 后者胜(配置层不该出现,兜底)

        var eff = new DeviceDef
        {
            Id = instance.Id,
            Name = instance.Name,
            Endpoint = instance.Endpoint,
            Description = instance.Description,
            Settings = instance.Settings,
            TemplateRef = instance.TemplateRef,
            Delta = instance.Delta,
            Tags = ExpandTags(template.Tags, "", masks, overrides),
            Groups = ExpandGroups(template.Groups, "", masks, overrides),
        };

        // 合入追加点:按目标组路径插入(空=设备根层);组不存在则创建
        foreach (var add in instance.Delta?.Adds ?? new())
        {
            if (add.Tag is null || string.IsNullOrWhiteSpace(add.Tag.Name)) continue;
            var target = string.IsNullOrWhiteSpace(add.GroupPath) ? eff.Tags : FindOrCreateGroup(eff, add.GroupPath).Tags;
            target.Add(add.Tag);
        }
        return eff;
    }

    private static List<TagDef> ExpandTags(List<TagDef> tags, string parentPath, HashSet<string> masks, Dictionary<string, TagOverride> overrides)
    {
        var result = new List<TagDef>(tags.Count);
        foreach (var t in tags)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) continue;
            string path = parentPath.Length == 0 ? t.Name : parentPath + "." + t.Name;
            if (masks.Contains(path)) continue;                                   // 屏蔽:剔除
            result.Add(overrides.TryGetValue(path, out var o) ? ApplyOverride(t, o) : t);   // 覆盖生成新对象;否则引用共享
        }
        return result;
    }

    private static List<TagGroupDef> ExpandGroups(List<TagGroupDef> groups, string parentPath, HashSet<string> masks, Dictionary<string, TagOverride> overrides)
    {
        var result = new List<TagGroupDef>(groups.Count);
        foreach (var g in groups)
        {
            string gp = parentPath.Length == 0 ? g.Name : parentPath + "." + g.Name;
            result.Add(new TagGroupDef
            {
                Id = g.Id,
                Name = g.Name,
                Description = g.Description,
                Tags = ExpandTags(g.Tags, gp, masks, overrides),
                SubGroups = ExpandGroups(g.SubGroups, gp, masks, overrides),
            });
        }
        return result;
    }

    /// <summary>克隆模板点并压上覆盖:仅非 null 字段生效(只存改动)。不改动模板原对象。</summary>
    private static TagDef ApplyOverride(TagDef t, TagOverride o) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Address = o.Address ?? t.Address,
        DataType = o.DataType ?? t.DataType,
        Access = o.Access ?? t.Access,
        ScanRateMs = o.ScanRateMs ?? t.ScanRateMs,
        Scaling = o.Scaling ?? t.Scaling,
        Deadband = t.Deadband,
        DeadbandType = t.DeadbandType,
        Description = o.Description ?? t.Description,
        EngUnit = o.EngUnit ?? t.EngUnit,
        Alarm = t.Alarm,
        StringLength = t.StringLength,
        TextEncoding = t.TextEncoding,
        WriteConfirmation = t.WriteConfirmation,
    };

    // 在有效设备内按 "组.子组" 路径找组,缺失则逐级创建
    private static TagGroupDef FindOrCreateGroup(DeviceDef eff, string groupPath)
    {
        var segs = groupPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<TagGroupDef> level = eff.Groups;
        TagGroupDef? cur = null;
        foreach (var seg in segs)
        {
            cur = level.FirstOrDefault(x => string.Equals(x.Name, seg, StringComparison.OrdinalIgnoreCase));
            if (cur is null)
            {
                cur = new TagGroupDef { Name = seg };
                level.Add(cur);
            }
            level = cur.SubGroups;
        }
        return cur!;   // segs 至少一段(调用方已判空 GroupPath)
    }
}

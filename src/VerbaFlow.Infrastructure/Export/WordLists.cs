using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>
/// Word's own bullets and numbering for list points, so someone editing the exported file gets lists that renumber,
/// continue on Enter and work with Word's list buttons. One bullet list definition and one numbered one are added to the
/// document (next to any a letterhead already has); every numbered list that starts at "1." gets its own instance that
/// restarts at 1, so two lists separated by a normal paragraph are numbered separately, as on the page.
/// </summary>
internal sealed class WordLists(MainDocumentPart main)
{
    private Numbering? numbering;
    private int bulletAbstract = -1, numberAbstract = -1, bulletNum = -1, currentNumbered = -1;

    /// <summary>Makes a paragraph a list point: "•" a bullet, "1." the start of a new numbered list, "2." and on its next point.
    /// The paragraph's text must not hold the marker itself.</summary>
    public void Apply(Paragraph para, string marker)
    {
        var numId = marker == "•" ? Bullet() : Numbered(restart: marker == "1." || currentNumbered < 0);
        var props = para.ParagraphProperties ??= new ParagraphProperties();
        props.NumberingProperties = new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = numId });
        // The bullet or number takes the look of the paragraph mark: give it the text's font and size.
        if (para.Elements<Run>().FirstOrDefault(r => r.RunProperties is not null)?.RunProperties is { } rp)
        {
            var mark = new ParagraphMarkRunProperties();
            foreach (var child in rp.ChildElements.Where(c => c is RunFonts or FontSize or FontSizeComplexScript or Color)) mark.Append(child.CloneNode(true));
            props.ParagraphMarkRunProperties = mark;
        }
    }

    private int Bullet()
    {
        if (bulletNum < 0)
        {
            bulletAbstract = AddAbstract(NumberFormatValues.Bullet, "•");
            bulletNum = AddInstance(bulletAbstract, restart: false);
        }
        return bulletNum;
    }

    private int Numbered(bool restart)
    {
        if (numberAbstract < 0) numberAbstract = AddAbstract(NumberFormatValues.Decimal, "%1.");
        if (restart) currentNumbered = AddInstance(numberAbstract, restart: true);
        return currentNumbered;
    }

    private Numbering Part()
    {
        if (numbering is not null) return numbering;
        var part = main.NumberingDefinitionsPart ?? main.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering ??= new Numbering();
        return numbering = part.Numbering;
    }

    private int AddAbstract(NumberFormatValues format, string text)
    {
        var n = Part();
        var id = n.Elements<AbstractNum>().Select(a => a.AbstractNumberId?.Value ?? 0).DefaultIfEmpty(-1).Max() + 1;
        var level = new Level(
            new StartNumberingValue { Val = 1 },
            new NumberingFormat { Val = format },
            new LevelText { Val = text },
            new LevelJustification { Val = LevelJustificationValues.Left },
            new PreviousParagraphProperties(new Indentation { Left = "425", Hanging = "425" })) { LevelIndex = 0 };
        var abstractNum = new AbstractNum(new MultiLevelType { Val = MultiLevelValues.SingleLevel }, level) { AbstractNumberId = id };
        // Word wants every list definition before the list instances.
        if (n.Elements<AbstractNum>().LastOrDefault() is { } lastAbstract) lastAbstract.InsertAfterSelf(abstractNum);
        else if (n.Elements<NumberingInstance>().FirstOrDefault() is { } firstInstance) firstInstance.InsertBeforeSelf(abstractNum);
        else if (n.Elements<NumberingPictureBullet>().LastOrDefault() is { } picture) picture.InsertAfterSelf(abstractNum);
        else n.PrependChild(abstractNum);
        return id;
    }

    private int AddInstance(int abstractId, bool restart)
    {
        var n = Part();
        var id = n.Elements<NumberingInstance>().Select(i => i.NumberID?.Value ?? 0).DefaultIfEmpty(0).Max() + 1;
        var instance = new NumberingInstance(new AbstractNumId { Val = abstractId }) { NumberID = id };
        if (restart) instance.Append(new LevelOverride(new StartOverrideNumberingValue { Val = 1 }) { LevelIndex = 0 });
        if (n.Elements<NumberingInstance>().LastOrDefault() is { } last) last.InsertAfterSelf(instance);
        else if (n.Elements<AbstractNum>().LastOrDefault() is { } lastAbstract) lastAbstract.InsertAfterSelf(instance);
        else n.Append(instance);
        return id;
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public partial class SignatureHelpState : ObservableObject
{
    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private CommandSignature? _currentSignature;

    [ObservableProperty]
    private int _overloadCount;

    [ObservableProperty]
    private int _selectedOverloadIndex;

    [ObservableProperty]
    private int _activeParameterIndex = -1;

    [ObservableProperty]
    private IReadOnlyList<SignaturePart> _signatureParts = [];

    [ObservableProperty]
    private string _activeParameterDescription = "";

    [ObservableProperty]
    private bool _showParameterDescription;

    private CommandSignatureSet? _currentSet;

    public void Show(CommandSignatureSet signatureSet, int paramIndex, string[] typedArgs)
    {
        _currentSet = signatureSet;
        OverloadCount = signatureSet.Signatures.Count;

        var bestIndex = FindBestOverload(signatureSet, paramIndex, typedArgs);
        SelectedOverloadIndex = bestIndex;
        ActiveParameterIndex = paramIndex;
        CurrentSignature = signatureSet.Signatures[bestIndex];
        SignatureParts = BuildParts(CurrentSignature, paramIndex);
        ActiveParameterDescription = GetParamDescription(CurrentSignature, paramIndex);
        ShowParameterDescription = ShouldShowParamDescription(CurrentSignature);
        IsVisible = true;
    }

    public void UpdateParameterIndex(int paramIndex)
    {
        if (_currentSet is null || !IsVisible)
        {
            return;
        }

        ActiveParameterIndex = paramIndex;
        if (CurrentSignature is not null)
        {
            SignatureParts = BuildParts(CurrentSignature, paramIndex);
            ActiveParameterDescription = GetParamDescription(CurrentSignature, paramIndex);
            ShowParameterDescription = ShouldShowParamDescription(CurrentSignature);
        }
    }

    public void NextOverload()
    {
        if (_currentSet is null || OverloadCount <= 1)
        {
            return;
        }

        SelectedOverloadIndex = (SelectedOverloadIndex + 1) % OverloadCount;
        CurrentSignature = _currentSet.Signatures[SelectedOverloadIndex];
        SignatureParts = BuildParts(CurrentSignature, ActiveParameterIndex);
        ActiveParameterDescription = GetParamDescription(CurrentSignature, ActiveParameterIndex);
        ShowParameterDescription = ShouldShowParamDescription(CurrentSignature);
    }

    public void PreviousOverload()
    {
        if (_currentSet is null || OverloadCount <= 1)
        {
            return;
        }

        SelectedOverloadIndex = (SelectedOverloadIndex - 1 + OverloadCount) % OverloadCount;
        CurrentSignature = _currentSet.Signatures[SelectedOverloadIndex];
        SignatureParts = BuildParts(CurrentSignature, ActiveParameterIndex);
        ActiveParameterDescription = GetParamDescription(CurrentSignature, ActiveParameterIndex);
        ShowParameterDescription = ShouldShowParamDescription(CurrentSignature);
    }

    public void Dismiss()
    {
        IsVisible = false;
        _currentSet = null;
        CurrentSignature = null;
        SignatureParts = [];
        ActiveParameterDescription = "";
        ShowParameterDescription = false;
    }

    private bool ShouldShowParamDescription(CommandSignature signature)
    {
        if (string.IsNullOrEmpty(ActiveParameterDescription))
        {
            return false;
        }

        return !string.Equals(ActiveParameterDescription, signature.UsageHint, StringComparison.Ordinal);
    }

    internal static IReadOnlyList<SignaturePart> BuildParts(CommandSignature signature, int activeParamIndex)
    {
        var parts = new List<SignaturePart>();

        // Add aliases as verb
        if (signature.Aliases.Count > 0)
        {
            parts.Add(new SignaturePart(signature.Aliases[0], false, false));
        }

        for (int i = 0; i < signature.Parameters.Count; i++)
        {
            parts.Add(new SignaturePart(" ", false, false));
            var param = signature.Parameters[i];
            // A trailing repeatable parameter (e.g. CROSS's runway list) renders with an ellipsis.
            var name = param.Repeatable ? $"{param.Name}…" : param.Name;
            // Literals render as plain text, variables in [brackets], optional variables in [name?]
            var text =
                param.IsLiteral ? name
                : param.IsOptional ? $"[{name}?]"
                : $"[{name}]";
            // A trailing repeatable parameter stays highlighted once the cursor reaches or passes it.
            bool isTrailingRepeatable = param.Repeatable && i == signature.Parameters.Count - 1;
            var isActive = i == activeParamIndex || (isTrailingRepeatable && activeParamIndex >= i);
            parts.Add(new SignaturePart(text, !param.IsLiteral, isActive));
        }

        return parts;
    }

    private static string GetParamDescription(CommandSignature signature, int paramIndex)
    {
        if (paramIndex < 0 || signature.Parameters.Count == 0)
        {
            return signature.UsageHint ?? "";
        }

        var idx = paramIndex;
        if (idx >= signature.Parameters.Count)
        {
            // Past the declared params, but a trailing repeatable one keeps describing itself.
            int last = signature.Parameters.Count - 1;
            if (!signature.Parameters[last].Repeatable)
            {
                return signature.UsageHint ?? "";
            }
            idx = last;
        }

        var param = signature.Parameters[idx];
        if (param.IsLiteral)
        {
            return signature.UsageHint ?? "";
        }

        return $"{param.Name}: {param.TypeHint}";
    }

    private static int FindBestOverload(CommandSignatureSet set, int paramIndex, string[] typedArgs)
    {
        if (set.Signatures.Count <= 1)
        {
            return 0;
        }

        // Score each overload: prefer the one whose parameter count matches typed args best
        int bestScore = -1;
        int bestIndex = 0;

        for (int i = 0; i < set.Signatures.Count; i++)
        {
            var sig = set.Signatures[i];

            // Eliminate overloads where a literal-position parameter contradicts what the user
            // typed. For args the user has finished (j < paramIndex) require an exact match;
            // for the in-progress arg (j == paramIndex, no trailing space) require the literal
            // to start with the typed prefix so "CTO R" still keeps RH/RT eligible.
            bool eliminated = false;
            for (int j = 0; j < Math.Min(typedArgs.Length, sig.Parameters.Count); j++)
            {
                var litParam = sig.Parameters[j];
                if (!litParam.IsLiteral)
                {
                    continue;
                }

                if (j < paramIndex)
                {
                    if (!string.Equals(typedArgs[j], litParam.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        eliminated = true;
                        break;
                    }
                }
                else
                {
                    if (!litParam.Name.StartsWith(typedArgs[j], StringComparison.OrdinalIgnoreCase))
                    {
                        eliminated = true;
                        break;
                    }
                }
            }

            if (eliminated)
            {
                continue;
            }

            int score = 0;

            // Prefer overloads that still have a parameter slot at the cursor's current
            // position. Without this, "ELB 28L " (trailing space, paramIndex=1) keeps showing
            // the 1-arg overload even though the cursor has moved past it.
            if (paramIndex >= 0 && sig.Parameters.Count > paramIndex)
            {
                score += 30;
            }

            // Prefer overloads with parameters when args are typed
            if (typedArgs.Length > 0 && sig.Parameters.Count > 0)
            {
                score += 10;
            }

            // Prefer overloads that match the arg count
            if (sig.Parameters.Count == typedArgs.Length)
            {
                score += 5;
            }
            else if (sig.Parameters.Count > typedArgs.Length)
            {
                score += 2;
            }

            // Check if typed args match known literal parameters
            for (int j = 0; j < Math.Min(typedArgs.Length, sig.Parameters.Count); j++)
            {
                var param = sig.Parameters[j];
                if (param.IsLiteral && string.Equals(typedArgs[j], param.Name, StringComparison.OrdinalIgnoreCase))
                {
                    score += 20;
                }
            }

            // Prefer bare overload when no args typed
            if (typedArgs.Length == 0 && sig.Parameters.Count == 0)
            {
                score += 8;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return bestIndex;
    }
}

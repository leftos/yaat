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

        var bestIndex = FindBestOverload(signatureSet, typedArgs);
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
            // Literals render as plain text, variables in [brackets]
            var text = param.IsLiteral ? param.Name : $"[{param.Name}]";
            var isActive = i == activeParamIndex;
            parts.Add(new SignaturePart(text, !param.IsLiteral, isActive));
        }

        return parts;
    }

    private static string GetParamDescription(CommandSignature signature, int paramIndex)
    {
        if (paramIndex < 0 || paramIndex >= signature.Parameters.Count)
        {
            return signature.UsageHint ?? "";
        }

        var param = signature.Parameters[paramIndex];
        if (param.IsLiteral)
        {
            return signature.UsageHint ?? "";
        }

        return $"{param.Name}: {param.TypeHint}";
    }

    private static int FindBestOverload(CommandSignatureSet set, string[] typedArgs)
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
            int score = 0;

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

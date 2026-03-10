namespace PropertySearch.Api.Validation;

public static class SearchValidation
{
    public static (bool valid, List<string> errors) ValidateKeyword(string keyword)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(keyword))
            errors.Add("Keyword cannot be empty");
        else if (keyword.Trim().Length < 2)
            errors.Add("Keyword must be at least 2 characters");

        if (keyword?.Length > 200)
            errors.Add("Keyword too long (max 200 characters)");

        if (keyword?.Any(c => c is '<' or '>' or '{' or '}') == true)
            errors.Add("Keyword contains invalid characters");

        return (errors.Count == 0, errors);
    }
}
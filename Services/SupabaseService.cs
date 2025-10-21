namespace AkariApi.Services;
public class SupabaseService
{
    private readonly Supabase.Client _client;
    private readonly Supabase.Client _adminClient;

    public SupabaseService(IEnumerable<Supabase.Client> clients)
    {
        _client = clients.First();
        _adminClient = clients.Last();
    }

    public Supabase.Client Client => _client;
    public Supabase.Client AdminClient => _adminClient;

    public async Task InitializeAsync()
    {
        await _client.InitializeAsync();
        await _adminClient.InitializeAsync();
    }
}
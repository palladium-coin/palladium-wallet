using PalladiumWallet.Core.Wallet.Ur;
using Xunit;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>
/// Golden vectors made by an independent implementation of BC-UR (Foundation Devices' foundation-ur-py, the Python
/// port of Blockchain Commons' reference) for pseudo-random PSBT-sized payloads wrapped as <c>crypto-psbt</c>: our encoder
/// must print the very same frames and our decoder must rebuild the payload from them.
/// </summary>
public class UrReferenceVectorTests
{
    public static IEnumerable<object[]> Vectors()
    {
        yield return [ "ea107d906941b28de10e421e67eb1b5aa03a80a64a31d019a1ab744e9e539e95ec506bfbdd205768", 100, new[]
        {
            "ur:crypto-psbt/hddewdbekimhinfpprlgvybafwckiowmcwhtnbftlaolgeehticfoypyjyglnngunnmdwpgdjezoutcxhgislfctwmbd",
        } ];
        yield return [ "63b7ef186d7193f86ca8e0248262522de3bb62f1a67aced86a20dcef9b6c38a888cac275d827cfd603ed6e30b00bbdb804864e11dd5c8e214dcfb9f355ff9f5c032b2110b1e23ceb475b66e447294c9facc0221eb3ed1d628ea72e8f4e80ee68d939a37d30", 100, new[]
        {
            "ur:crypto-psbt/1-2/lpadaocsiocydsbktoolhdeehdihiarlwscsjnjsmuyajzpdvtdklfidgmdpvlrkidwnolkntotpimcxuowsndjzetpdlosgsakptpditktbaxwejtdypfbdryroaalnwdrnzmec",
            "ur:crypto-psbt/2-2/lpaoaocsiocydsbktoolhdeeglbyuthhmnclgttkrhwfgozmnehhaxdnclbepavofnwmflhpiyvefldtgsnepsrtcpckqdwecaidmnosdmmygllawyistaesotkidyaefzzspsca",
            "ur:crypto-psbt/3-2/lpaxaocsiocydsbktoolhdeecmjyrnwmhsescxrndrbdeshglbkslygajkfsgmhkhycyvyclpdfndpasmhjoempscyrpfrdiurchhflavyhkgtjnlahdineyckskeelnpmbtayvs",
            "ur:crypto-psbt/4-2/lpaaaocsiocydsbktoolhdeehdihiarlwscsjnjsmuyajzpdvtdklfidgmdpvlrkidwnolkntotpimcxuowsndjzetpdlosgsakptpditktbaxwejtdypfbdryroaalncnpaettp",
            "ur:crypto-psbt/5-2/lpahaocsiocydsbktoolhdeecmjyrnwmhsescxrndrbdeshglbkslygajkfsgmhkhycyvyclpdfndpasmhjoempscyrpfrdiurchhflavyhkgtjnlahdineyckskeelnmdtsjohs",
            "ur:crypto-psbt/6-2/lpamaocsiocydsbktoolhdeeglbyuthhmnclgttkrhwfgozmnehhaxdnclbepavofnwmflhpiyvefldtgsnepsrtcpckqdwecaidmnosdmmygllawyistaesotkidyaetafgaddw",
        } ];
        yield return [ "b6b000fbb1d854523edaa9bc467e3018219794248cb309212c68c09a115818f6e31b8a64ca79d1608f48a7e8db7abdcd4d2d0d89dc6a1a85be3c22be0e5a63b457b9cd2cf4fcc20994635fe3022e604d173e1efbec9eb8cae5cfaec4954f69eaa8ad9e476f963e03605cb06081ac8660b37fa2ae378a85d69aa8646365622f350b3e4a34c8193f356b8dbd9ed528ab1f35d3bbadeba93adc3466b431674a456527d77b6ddafd5b08aad1634a3df3420530832b8b8f47c8d7ee7c6fb564a3f5151c19122d2aad8970601329d4fcfd26c350cc375473e377a5cd48649bf0f75c89ec321409daf206b907df43ddad18ab54eef83bf2cd367b98a21d6e4e18951a98d3d449ebe70500f05406cc528106f9d69fccbcfd7687a5d5251d0eae17662128a9e7eaa1100a6528dff53f40", 60, new[]
        {
            "ur:crypto-psbt/1-6/lpadamcfaddlcyehdiyawlhdeohkaddwrppfaezopatpghgmfmtnptrffgkbdycsclmsmwdklkqdascldwisrtnybyhdcsynvlcwleiesgkktthnmyfdosvsuyknrysnonenbzwt",
            "ur:crypto-psbt/2-6/lpaoamcfaddlcyehdiyawlhdeogtdpbtlduoimcylprnfncprnbahtiaqzhgrhsndwwkztsaasmwiahevlaodmhngtchfmckzowpnnrosgvwtkplssmdgwinwdpdpmnnjtlueyps",
            "ur:crypto-psbt/3-6/lpaxamcfaddlcyehdiyawlhdeofljlmtfmaxhnhhpfhnlypslnhnqdlboeplemlelptbnypdieiaihiddlecbdfmgeeespcffhecjelgrynntldepyctecterkpmwmptnlktueny",
            "ur:crypto-psbt/4-6/lpaaamcfaddlcyehdiyawlhdeoftuoeeiyqzehiogefeihditskgjntnzchpaypkttiagefswffwahdylsdnlumyflsptswykejlreieotykbzcecfbgdpdrpmldjohnrnwzsfhd",
            "ur:crypto-psbt/5-6/lpahamcfaddlcyehdiyawlhdeobwdttyztzcdssrgdsfemghjkvlktonsnfdiendwtylhhldwpeybbastnwzamrhaturfxutpmcspyghwyyafrwzsnenkgmkoecajtglwmdpswhk",
            "ur:crypto-psbt/6-6/lpamamcfaddlcyehdiyawlhdeocsmdcymktetygawmvdahaewtghamsfgmlyamyttbnesfrfzckoltontldacabaplchiycldeptvdwdoybebkihdeurykfhfzaeaeaefnlsotrt",
            "ur:crypto-psbt/7-6/lpatamcfaddlcyehdiyawlhdeoihdsrortielpjpbysavyluoygwtpinbtjyestalfdrcedtimhgvdylkkfrntrsotwmkktbjewfesaxrskgsggynytdweswhfdkndsocnuosolu",
            "ur:crypto-psbt/8-6/lpayamcfaddlcyehdiyawlhdeogtdpbtlduoimcylprnfncprnbahtiaqzhgrhsndwwkztsaasmwiahevlaodmhngtchfmckzowpnnrosgvwtkplssmdgwinwdpdpmnndivwrkem",
            "ur:crypto-psbt/9-6/lpasamcfaddlcyehdiyawlhdeobztdjywpntmodprdmuvdpkfrbtjepkkgcfihwtlfdtnnrhytmwzmwsbnletibwwzoxykbwhknepmhgetbsecurserncenslfsgetdrutnebeft",
            "ur:crypto-psbt/10-6/lpbkamcfaddlcyehdiyawlhdeokiqdoehdrlgyfrzsdavelugycwueonheykfhcxghretimdmsclhngmpscklapabtztctylfxhtuewlckjerteeprbtcsytcmdkndsotpzsjeva",
            "ur:crypto-psbt/11-6/lpbdamcfaddlcyehdiyawlhdeogtdpbtlduoimcylprnfncprnbahtiaqzhgrhsndwwkztsaasmwiahevlaodmhngtchfmckzowpnnrosgvwtkplssmdgwinwdpdpmnntbdyaagu",
            "ur:crypto-psbt/12-6/lpbnamcfaddlcyehdiyawlhdeogtdpbtlduoimcylprnfncprnbahtiaqzhgrhsndwwkztsaasmwiahevlaodmhngtchfmckzowpnnrosgvwtkplssmdgwinwdpdpmnnrnhkcmam",
            "ur:crypto-psbt/13-6/lpbtamcfaddlcyehdiyawlhdeoftuoeeiyqzehiogefeihditskgjntnzchpaypkttiagefswffwahdylsdnlumyflsptswykejlreieotykbzcecfbgdpdrpmldjohnamgazsos",
            "ur:crypto-psbt/14-6/lpbaamcfaddlcyehdiyawlhdeobwdttyztzcdssrgdsfemghjkvlktonsnfdiendwtylhhldwpeybbastnwzamrhaturfxutpmcspyghwyyafrwzsnenkgmkoecajtglwzwtdack",
        } ];
        yield return [ "db32c9b420ef54df42717bbafa67bb74ea5d14f949ecd4660bef6f5a378152922f00a00883953c6395c3a37476b6f83d96c0eabbf88ff1a8061b49e62674950cbd406945a9b1ac95f467cc25737b9a99d4a5035432f8d0efc0cb0c981816676634af6648322644896bae346b1a514fbb562bfa8f736e8a080a7949e077f45587d00110ea1fc623f78e6f0cd835956da875ad7204724713fff24d4ec1d96478af3782a0cda4f8de6d5c4894d8913f8542e6cf56f473868526b12f584af5e04e6340d8d9059f272dcf6debbfc4d2ec01e491e592607d20101e87420f1077eb70fe969f7b26fcdfb87f401a6a67d9e9d416a18c7160b23e47de00fb4eba3bcfeb6358822e05e3abf90fb4f91c51c4d5705f433cbcbc30beab7715dee9ecb012c0451942a04d11b5c3b76d7fb256f41dad5d5571955c8f75dd73718fb78968c028b9bd4aa5bc639e59a1d29e3731ac63af74fd5af7aa62965d63986530ae3a614d34cd990b13d9eb2d416e17d9542fce38ee72d9b1ff76aaad140ded02c7aafb96f252e7655f19496c2ffa561ed7de62cd908b59566a247343cf919e437d8923eb05faa6e25296dfec38f1163013182931389eba692aeb3f02b36d5df9da9241298b07b9ade4b0038f8d19d94a86937cc847357653d1a3970e34ea3dccac010ad4f9f5bda266a33c107246e7cbe20d0b37aa1e755ea676130673832fe5f6109cff14f1853774b35a702780f0b0a88ca52c01b3b9a6a79e5afadbe80682dcf113d89d055e0a9b74568f9115b9fd8165413e72ac5baf7b32e48dd7d89d088ebd8fad948777db6ffdbf9910daf65da1e7e8536e6681301559c3f881d2c601266776095c1df2cce1ed3db3cd7f3a73e41fb9963b08f82c69ff2e391faa3b8bf75277070773e2b097a20398e1d50af4787c70523d3fb5f222f826d82f87578bef46d82770ddfe72afb3096afa0df2431288d015100973eeea38af3ebeeacd3003f25b4a624114d01fca35c7c5236a852ed59ed56a13a2be70a6b665a6222c5e872e1abf5fbfc522bfaac07db9b75181a99ec39fb71c7029c090c059e55dc2cb8ea3a4655d7ffa76f52a5774161eee6d1a66e9a1cd276ffb3188ce84dbe3bdeeec3e23089819b8a1b6c8e0051585d33d659019405fcbc12dd165128724d3d13b4481421e40711af4409179307dbbbf001f97e1ab22907b3936b03e81157c76acd48ba24ae6443adc6fa959b0e4ada860696ab229d9a0ebcc6bd2e888c713eb692be759caa79bb5d4dad1cf5da7ca2cbe3d85750224918373a1631f1bd06bcf3280caca6439f20c7fb7c5340ade849e8124da43f988af71a74b019e4e797f6f208dbd6429237160153a1d768d9dee2dd8cd3d56dfb2b5d871dea15013be89d9f8c5fa8e4d4b19d126bcac8026d0", 80, new[]
        {
            "ur:crypto-psbt/1-13/lpadbtcfaxwmcysfeyjzldhdglhkaxvsuyeysoqzcxwsghurfwjskgrdzsiorkjywdhlbbytgawptyiybdwsjlhtemlygmmodlaenbaylsmdfniamdsrotjykorpyafsmtrtwdrkyamywnpdamcwgavadsjymdbnryfzinfeptpapsmdwkiosffgwsoxbd",
            "ur:crypto-psbt/2-13/lpaobtcfaxwmcysfeyjzldhdgldajkkgnynltyonaxgheyyatiwsrtsbbnmkcscmioiyeepeiyfdeydsfyldjepleejecygygwrkhfdnzsmyjkjtleaybkkkgavtktwkgolttiadbewdctswcnylmnjlbntpecmdjnpdkppmjpaajpflbwzmwzjtsaztde",
            "ur:crypto-psbt/3-13/lpaxbtcfaxwmcysfeyjzldhdglgtglsetaiekspeemlfnbsnoxyauejnhhfdmwtpmefhlpfwvatkhfwkjklnlpdspadlhdgeykvtgliafztptaahnedidptkjnwmrssstdwpadvemevwmohnkicxbeckltfwbsbektwmjozemtnekgdszturroendtolsb",
            "ur:crypto-psbt/4-13/lpaabtcfaxwmcysfeyjzldhdgllbfzcyimiotawltycmoylkjshnprfmflueaezoglrdfrtkwmiahdlfdmahvlpyytbsqzytcegysstljohefxfnrfrfdyrnpyktbzuewlwppfbgrtfecffwnbgtbyresrrljnlbprhfwkcapmhlgojsmdhhmyvowleyde",
            "ur:crypto-psbt/5-13/lpahbtcfaxwmcysfeyjzldhdglkputjkjsmyrlldisrtderhrygeonrfiannhkoytdnnemehpsiapejyzchtylpkidmthliamkihdyplfthsgteesnnlbdbwtawmdpfpjtchtaghdltoetwyjptapazmkopkpmbbbtweaostpkzomtwzgmvdihskhdjebs",
            "ur:crypto-psbt/6-13/lpambtcfaxwmcysfeyjzldhdglhecfgajzdlzshfcktsueidsnmhluhkhfimdkjkfxtkmennfxkildcnwmahzsolvogmmturwpetwncmdybwcsdtehetnnrdindrwmfhaoqdjnhlyttnmofpdtluatrhpmvepfaxmylgcftagelnmukespflecdpghsrjp",
            "ur:crypto-psbt/7-13/lpatbtcfaxwmcysfeyjzldhdglkoguttotmsbaeewdfssfpsadbktyytykryoeiyotfnbejpfgvdsbvobtbdempkckkphyolkobwamjklsdlvwynbenszmbbwnlpemjyqdhtjodilawtpfpdlkondwadqdrholosnnhtzsuyvsamlfuownbwtpdwuridas",
            "ur:crypto-psbt/8-13/lpaybtcfaxwmcysfeyjzldhdglntahhybkndjyhfmymebzrhzclyihfpfmjppshppekgeyvelgtstpntaymnrymypmmwltktuyjlzcrsnlbetnynhloyvdvsgujtiylydybzhksryalytdswaddsiokoashhcawzsfvywefsqdsnlbftjkvectihgmjpze",
            "ur:crypto-psbt/9-13/lpasbtcfaxwmcysfeyjzldhdglrhmtfrayyadwinzmdmesctpkfrluylgmktatatjkvopfmsoeaxmkvytlbkwkkskejogmfsfhrewzcpyadstpdllthgluwsfgtpdijoutzejppeqdasimzsbtwzfxbglotibzbeasjkwywdetpefmrnwdsndyvsmolrfm",
            "ur:crypto-psbt/10-13/lpbkbtcfaxwmcysfeyjzldhdglaxwzhpgeidfpbbtictsgecstskcnimlpdmtlnntlimbwoernjoolrpiholcpdwhyltdmcyrshersskcprspkrtkirhrlgylyptnnsrnerlcejodtrtmhrthkvwhlsasbmnotoxihhllbzskoykdrhgjycmcktnoyeogh",
            "ur:crypto-psbt/11-13/lpbdbtcfaxwmcysfeyjzldhdglwyjncyiywloysndijlzoehlotolruyvlrywywpfmcnaymkcfrooyrpspvtahbzlptefsihmhcffzhesbsedpttihbgltdktettfrfylyfwckfzjscywkfzmekkdykirkrsaectmsvypycpmhkgesenpffmlysrrowppm",
            "ur:crypto-psbt/12-13/lpbnbtcfaxwmcysfeyjzldhdglbzkekopstyluoegevafyftuojlpthkpfvepmpdhninimprdttanbwmsfjetdvslostbwwmindnvdhksgosndretytntttkhlossgdwrnfslpkpaodkmelsjkoyiactcwtijetkeylasgsgieeswzbnlbrlskptsntojp",
            "ur:crypto-psbt/13-13/lpbtbtcfaxwmcysfeyjzldhdgleebkuelrnnlydktnfxytlopejsosgradnnglkklbjlcxlgryiedtcnjshnbzftcakolgntwydptpsnfshfurprretpjsueoygdbwrnldtayaskzsmngtgrcfttdsrfpsladstiaeaeaeaeaeaeaeaeaeaeaedrvlhlue",
            "ur:crypto-psbt/14-13/lpbabtcfaxwmcysfeyjzldhdglamcyoyrlcaeovofmetlerymyvywtvlpsbtneatptmolpiobkmehlfevtwdmdzttltessgtsretgyckqdlndkgeoxzofstoctnsbwaomwjkltvaadgoiawldlmhglhelumhdabseysnjonsvlemfhwlfncxytaeasonch",
            "ur:crypto-psbt/15-13/lpbsbtcfaxwmcysfeyjzldhdglimfnjzswqdgmgrnnwtvwrppmbscwioylftpmgudmtegykisardyainvojtehfxjssposbgkpkncnlkrdyatpldisiyvyjsyntiurwzhgtteciosahslosetewpjppktpioampflatbfmtssoieoskiwdwmgejpwddaas",
            "ur:crypto-psbt/16-13/lpbebtcfaxwmcysfeyjzldhdglptbslamnahykjpgotdwpehgmwtsabkfhwpvocptibbbgindyqdwnrnkkwypdrepfvobkwdecfwdajpoxfgahfyvskkmtenwzfmkpfhrhsfoyamaobsnelgcsylfpsgonuofrcpsfvywefsqdsnlbftjkvectlazouyee",
            "ur:crypto-psbt/17-13/lpbybtcfaxwmcysfeyjzldhdglylfrcespsgesiomuvwfntltthhrdfhrngwnybajlrtmoltvaideswemyswplvyleuoyaplylryuyzmwnkemefzgeimdrtygdcavwtedwjogottdneyimpaneztweosossnwdatmnwpskfxlnpkctgockdtmkcagmfxjo",
            "ur:crypto-psbt/18-13/lpbgbtcfaxwmcysfeyjzldhdglfrhswlvoldmneymyhlfwfpuekthdrkrdyacsvdmtsboyttwkuoheenwmkngdnblgfmdneotdtnseayhdamfmwndlatmelgecptbnfgcngsssrtmswmftglhfceztcnftiyadnbcthpbtsrgemestlstndmsrmhhgnbgo",
            "ur:crypto-psbt/19-13/lpbwbtcfaxwmcysfeyjzldhdglbgdnjsdpehkngdkgrlgdmybgweatcwcfdlnnwzkoetcxfswnfgcaylbtjyntpsjscfvlgefdwnenjnwmftsrdmbaotwtcnpmamtibtmoontardwysecsoswfeytsnddkfrchaabalkwysevsbytbcnvlkndmmeuoaddm",
            "ur:crypto-psbt/20-13/lpbbbtcfaxwmcysfeyjzldhdglrhmtfrayyadwinzmdmesctpkfrluylgmktatatjkvopfmsoeaxmkvytlbkwkkskejogmfsfhrewzcpyadstpdllthgluwsfgtpdijoutzejppeqdasimzsbtwzfxbglotibzbeasjkwywdetpefmrnwdsndypejnlpfr",
            "ur:crypto-psbt/21-13/lpbzbtcfaxwmcysfeyjzldhdgllbfzcyimiotawltycmoylkjshnprfmflueaezoglrdfrtkwmiahdlfdmahvlpyytbsqzytcegysstljohefxfnrfrfdyrnpyktbzuewlwppfbgrtfecffwnbgtbyresrrljnlbprhfwkcapmhlgojsmdhhmywdbzfzpe",
            "ur:crypto-psbt/22-13/lpcmbtcfaxwmcysfeyjzldhdglzmayecztlkyarpkgbwmnwslslecerhdlrhiytpseftynsrttsatardghdsrsvdknsbtsadpslsspbehnuyjkwfchtslyperpmoptaydyaosedkcxtseozobtfeasntbawklspetkvofnhkswldntutjsfyjomnbyidcw",
            "ur:crypto-psbt/23-13/lpchbtcfaxwmcysfeyjzldhdglrpfswtbdotrhwlbsamfxmokbtynyfrpkcfrfmkidskspwyetaxwsnldmwfhfrfcktnzeksnlheiacsiecsbnoerfwminbweybkhnztldsrvyattohygeontegtwdaygegopmiotebeidmymncxneuypachmeenmkwpfr",
            "ur:crypto-psbt/24-13/lpcscsbtcfaxwmcysfeyjzldhdglmnsfctrkprntvaahgamsprwlwmlofmdystcyvwlktygocwnsaopldtytttcxhgjllfjkwehshgmspdfratzogdkegyhlyahgntuodtgyisfwhshhwthldpmhadvonttbskswsspdlkiadlhyrhhlspntjtclhecytafh",
            "ur:crypto-psbt/25-13/lpcscfbtcfaxwmcysfeyjzldhdglgtglsetaiekspeemlfnbsnoxyauejnhhfdmwtpmefhlpfwvatkhfwkjklnlpdspadlhdgeykvtgliafztptaahnedidptkjnwmrssstdwpadvemevwmohnkicxbeckltfwbsbektwmjozemtnekgdszturroswckendk",
        } ];
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Encoder_prints_the_reference_frames(string psbtHex, int fragment, string[] frames)
    {
        var enc = UrEncoder.ForPsbt(Convert.FromHexString(psbtHex), fragment);
        Assert.Equal(frames, frames.Select(_ => enc.NextPart()).ToArray());
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Decoder_rebuilds_the_payload_from_the_reference_frames(string psbtHex, int fragment, string[] frames)
    {
        var d = new UrDecoder();
        foreach (var f in frames.TakeWhile(_ => !d.IsComplete))
            Assert.True(d.Receive(f));
        Assert.True(d.IsComplete);
        Assert.Equal(Convert.FromHexString(psbtHex), d.Psbt);
    }
}

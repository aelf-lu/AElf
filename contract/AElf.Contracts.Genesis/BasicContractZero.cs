using AElf.CSharp.Core.Extension;
using AElf.Sdk.CSharp;
using AElf.Standards.ACS0;
using AElf.Standards.ACS3;
using AElf.Types;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace AElf.Contracts.Genesis;

public partial class BasicContractZero : BasicContractZeroImplContainer.BasicContractZeroImplBase
{
    #region Views

    public override Int64Value CurrentContractSerialNumber(Empty input)
    {
        return new Int64Value { Value = State.ContractSerialNumber.Value };
    }

    public override ContractInfo GetContractInfo(Address input)
    {
        var info = State.ContractInfos[input];
        if (info == null) return new ContractInfo();

        return info;
    }

    public override Address GetContractAuthor(Address input)
    {
        var info = State.ContractInfos[input];
        return info?.Author;
    }

    public override Hash GetContractHash(Address input)
    {
        var info = State.ContractInfos[input];
        return info?.CodeHash;
    }

    public override Address GetContractAddressByName(Hash input)
    {
        var address = State.NameAddressMapping[input];
        return address;
    }

    public override SmartContractRegistration GetSmartContractRegistrationByAddress(Address input)
    {
        var info = State.ContractInfos[input];
        if (info == null) return null;

        return State.SmartContractRegistrations[info.CodeHash];
    }

    public override SmartContractRegistration GetSmartContractRegistrationByCodeHash(Hash input)
    {
        return State.SmartContractRegistrations[input];
    }

    public override Empty ValidateSystemContractAddress(ValidateSystemContractAddressInput input)
    {
        var actualAddress = GetContractAddressByName(input.SystemContractHashName);
        Assert(actualAddress == input.Address, "Address not expected.");
        return new Empty();
    }

    public override AuthorityInfo GetContractDeploymentController(Empty input)
    {
        return State.ContractDeploymentController.Value;
    }

    public override AuthorityInfo GetCodeCheckController(Empty input)
    {
        return State.CodeCheckController.Value;
    }

    public override ContractCodeHashList GetContractCodeHashListByDeployingBlockHeight(Int64Value input)
    {
        return State.ContractCodeHashListMap[input.Value];
    }

    public override Int32Value GetContractProposalExpirationTimePeriod(Empty input)
    {
        var expirationTimePeriod = GetCurrentContractProposalExpirationTimePeriod();
        return new Int32Value{ Value = expirationTimePeriod };
    }
    
    public override Address GetContractAddressByCodeHash(Hash input)
    {
        var registration = State.SmartContractRegistrations[input];
        return registration?.Address;
    }

    #endregion Views

    #region Actions

    public override Address DeploySystemSmartContract(SystemContractDeploymentInput input)
    {
        Assert(!State.Initialized.Value || !State.ContractDeploymentAuthorityRequired.Value,
            "System contract deployment failed.");
        RequireSenderAuthority();
        var name = input.Name;
        var category = input.Category;
        var code = input.Code.ToByteArray();
        var transactionMethodCallList = input.TransactionMethodCallList;

        // Context.Sender should be identical to Genesis contract address before initialization in production
        var address = DeploySmartContract(name, category, code, true, Context.Sender);

        if (transactionMethodCallList != null)
            foreach (var methodCall in transactionMethodCallList.Value)
                Context.SendInline(address, methodCall.MethodName, methodCall.Params);

        return address;
    }

    public override Hash ProposeNewContract(ContractDeploymentInput input)
    {
        // AssertDeploymentProposerAuthority(Context.Sender);
        var proposedContractInputHash = CalculateHashFromInput(input);
        RegisterContractProposingData(proposedContractInputHash);
        
        var expirationTimePeriod = GetCurrentContractProposalExpirationTimePeriod();

        // Create proposal for deployment
        var proposalCreationInput = new CreateProposalBySystemContractInput
        {
            ProposalInput = new CreateProposalInput
            {
                ToAddress = Context.Self,
                ContractMethodName =
                    nameof(BasicContractZeroImplContainer.BasicContractZeroImplBase.ProposeContractCodeCheck),
                Params = new ContractCodeCheckInput
                {
                    ContractInput = input.ToByteString(),
                    CodeCheckReleaseMethod = nameof(DeploySmartContract),
                    ProposedContractInputHash = proposedContractInputHash,
                    Category = input.Category,
                    IsSystemContract = false
                }.ToByteString(),
                OrganizationAddress = State.ContractDeploymentController.Value.OwnerAddress,
                ExpiredTime = Context.CurrentBlockTime.AddSeconds(expirationTimePeriod)
            },
            OriginProposer = Context.Sender
        };
        Context.SendInline(State.ContractDeploymentController.Value.ContractAddress,
            nameof(AuthorizationContractContainer.AuthorizationContractReferenceState
                .CreateProposalBySystemContract), proposalCreationInput.ToByteString());

        Context.Fire(new ContractProposed
        {
            ProposedContractInputHash = proposedContractInputHash
        });

        return proposedContractInputHash;
    }

    public override Hash ProposeUpdateContract(ContractUpdateInput input)
    {
        var proposedContractInputHash = CalculateHashFromInput(input);
        RegisterContractProposingData(proposedContractInputHash);

        var contractAddress = input.Address;
        var info = State.ContractInfos[contractAddress];
        Assert(info != null, "Contract not found.");
        AssertAuthorityByContractInfo(info, Context.Sender);

        var expirationTimePeriod = GetCurrentContractProposalExpirationTimePeriod();

        // Create proposal for contract update
        var proposalCreationInput = new CreateProposalBySystemContractInput
        {
            ProposalInput = new CreateProposalInput
            {
                ToAddress = Context.Self,
                ContractMethodName =
                    nameof(BasicContractZeroImplContainer.BasicContractZeroImplBase.ProposeContractCodeCheck),
                Params = new ContractCodeCheckInput
                {
                    ContractInput = input.ToByteString(),
                    CodeCheckReleaseMethod = nameof(UpdateSmartContract),
                    ProposedContractInputHash = proposedContractInputHash,
                    Category = info.Category,
                    IsSystemContract = info.IsSystemContract
                }.ToByteString(),
                OrganizationAddress = State.ContractDeploymentController.Value.OwnerAddress,
                ExpiredTime = Context.CurrentBlockTime.AddSeconds(expirationTimePeriod)
            },
            OriginProposer = Context.Sender
        };
        Context.SendInline(State.ContractDeploymentController.Value.ContractAddress,
            nameof(AuthorizationContractContainer.AuthorizationContractReferenceState
                .CreateProposalBySystemContract), proposalCreationInput);

        Context.Fire(new ContractProposed
        {
            ProposedContractInputHash = proposedContractInputHash
        });

        return proposedContractInputHash;
    }

    public override Hash ProposeContractCodeCheck(ContractCodeCheckInput input)
    {
        RequireSenderAuthority(State.ContractDeploymentController.Value.OwnerAddress);
        AssertCodeCheckProposingInput(input);
        var proposedContractInputHash = input.ProposedContractInputHash;
        var proposedInfo = State.ContractProposingInputMap[proposedContractInputHash];
        Assert(proposedInfo != null && proposedInfo.Status == ContractProposingInputStatus.Approved,
            "Invalid contract proposing status.");
        proposedInfo.Status = ContractProposingInputStatus.CodeCheckProposed;
        State.ContractProposingInputMap[proposedContractInputHash] = proposedInfo;

        var codeCheckController = State.CodeCheckController.Value;
        var proposalCreationInput = new CreateProposalBySystemContractInput
        {
            ProposalInput = new CreateProposalInput
            {
                ToAddress = Context.Self,
                ContractMethodName = input.CodeCheckReleaseMethod,
                Params = input.ContractInput,
                OrganizationAddress = codeCheckController.OwnerAddress,
                ExpiredTime = Context.CurrentBlockTime.AddSeconds(CodeCheckProposalExpirationTimePeriod)
            },
            OriginProposer = proposedInfo.Proposer
        };

        proposedInfo.ExpiredTime = proposalCreationInput.ProposalInput.ExpiredTime;
        State.ContractProposingInputMap[proposedContractInputHash] = proposedInfo;
        Context.SendInline(codeCheckController.ContractAddress,
            nameof(AuthorizationContractContainer.AuthorizationContractReferenceState
                .CreateProposalBySystemContract), proposalCreationInput);

        // Fire event to trigger BPs checking contract code
        Context.Fire(new CodeCheckRequired
        {
            Code = ExtractCodeFromContractCodeCheckInput(input),
            ProposedContractInputHash = proposedContractInputHash,
            Category = input.Category,
            IsSystemContract = input.IsSystemContract
        });

        return proposedContractInputHash;
    }

    public override Empty ReleaseApprovedContract(ReleaseContractInput input)
    {
        var contractProposingInput = State.ContractProposingInputMap[input.ProposedContractInputHash];
        Assert(
            contractProposingInput != null &&
            contractProposingInput.Status == ContractProposingInputStatus.Proposed &&
            contractProposingInput.Proposer == Context.Sender, "Invalid contract proposing status.");
        contractProposingInput.Status = ContractProposingInputStatus.Approved;
        State.ContractProposingInputMap[input.ProposedContractInputHash] = contractProposingInput;
        Context.SendInline(State.ContractDeploymentController.Value.ContractAddress,
            nameof(AuthorizationContractContainer.AuthorizationContractReferenceState.Release),
            input.ProposalId.ToByteString());
        return new Empty();
    }

    public override Empty ReleaseCodeCheckedContract(ReleaseContractInput input)
    {
        var contractProposingInput = State.ContractProposingInputMap[input.ProposedContractInputHash];

        Assert(
            contractProposingInput != null &&
            contractProposingInput.Status == ContractProposingInputStatus.CodeCheckProposed &&
            contractProposingInput.Proposer == Context.Sender, "Invalid contract proposing status.");
        contractProposingInput.Status = ContractProposingInputStatus.CodeChecked;
        State.ContractProposingInputMap[input.ProposedContractInputHash] = contractProposingInput;
        var codeCheckController = State.CodeCheckController.Value;
        Context.SendInline(codeCheckController.ContractAddress,
            nameof(AuthorizationContractContainer.AuthorizationContractReferenceState.Release), input.ProposalId);
        return new Empty();
    }


    public override Address DeploySmartContract(ContractDeploymentInput input)
    {
        RequireSenderAuthority(State.CodeCheckController.Value?.OwnerAddress);
        // AssertDeploymentProposerAuthority(Context.Origin);

        var inputHash = CalculateHashFromInput(input);
        TryClearContractProposingData(inputHash, out var contractProposingInput);

        var address =
            DeploySmartContract(null, input.Category, input.Code.ToByteArray(), false,
                DecideNonSystemContractAuthor(contractProposingInput?.Proposer, Context.Sender));
        return address;
    }

    public override Address UpdateSmartContract(ContractUpdateInput input)
    {
        var contractAddress = input.Address;
        var code = input.Code.ToByteArray();
        var info = State.ContractInfos[contractAddress];
        Assert(info != null, "Contract not found.");
        RequireSenderAuthority(State.CodeCheckController.Value?.OwnerAddress);
        var inputHash = CalculateHashFromInput(input);

        if (!TryClearContractProposingData(inputHash, out _))
            Assert(Context.Sender == info.Author, "No permission.");

        var oldCodeHash = info.CodeHash;
        var newCodeHash = HashHelper.ComputeFrom(code);
        Assert(oldCodeHash != newCodeHash, "Code is not changed.");

        Assert(State.SmartContractRegistrations[newCodeHash] == null, "Same code has been deployed before.");

        info.CodeHash = newCodeHash;
        info.Version++;
        State.ContractInfos[contractAddress] = info;

        var reg = new SmartContractRegistration
        {
            Category = info.Category,
            Code = ByteString.CopyFrom(code),
            CodeHash = newCodeHash,
            IsSystemContract = info.IsSystemContract,
            Version = info.Version,
            Address = contractAddress
        };

        State.SmartContractRegistrations[reg.CodeHash] = reg;

        Context.UpdateContract(contractAddress, reg, null);

        Context.Fire(new CodeUpdated
        {
            Address = contractAddress,
            OldCodeHash = oldCodeHash,
            NewCodeHash = newCodeHash,
            Version = info.Version
        });

        Context.LogDebug(() => "BasicContractZero - update success: " + contractAddress.ToBase58());
        return contractAddress;
    }

    public override Empty Initialize(InitializeInput input)
    {
        Assert(!State.Initialized.Value, "Contract zero already initialized.");
        Assert(Context.Sender == Context.Self, "No permission.");
        State.ContractDeploymentAuthorityRequired.Value = input.ContractDeploymentAuthorityRequired;
        State.Initialized.Value = true;
        return new Empty();
    }

    public override Empty SetInitialControllerAddress(Address input)
    {
        Assert(State.ContractDeploymentController.Value == null && State.CodeCheckController.Value == null,
            "Genesis owner already initialized");
        var parliamentContractAddress =
            GetContractAddressByName(SmartContractConstants.ParliamentContractSystemHashName);
        Assert(Context.Sender == parliamentContractAddress, "Unauthorized to initialize genesis contract.");
        Assert(input != null, "Genesis Owner should not be null.");
        var defaultAuthority = new AuthorityInfo
        {
            OwnerAddress = input,
            ContractAddress = parliamentContractAddress
        };
        State.ContractDeploymentController.Value = defaultAuthority;
        State.CodeCheckController.Value = defaultAuthority;
        return new Empty();
    }

    public override Empty ChangeContractDeploymentController(AuthorityInfo input)
    {
        AssertSenderAddressWith(State.ContractDeploymentController.Value.OwnerAddress);
        var organizationExist = CheckOrganizationExist(input);
        Assert(organizationExist, "Invalid authority input.");
        State.ContractDeploymentController.Value = input;
        return new Empty();
    }

    public override Empty ChangeCodeCheckController(AuthorityInfo input)
    {
        AssertSenderAddressWith(State.CodeCheckController.Value.OwnerAddress);
        Assert(CheckOrganizationExist(input),
            "Invalid authority input.");
        State.CodeCheckController.Value = input;
        return new Empty();
    }

    public override Empty SetContractProposerRequiredState(BoolValue input)
    {
        Assert(!State.Initialized.Value, "Genesis contract already initialized.");
        var address = GetContractAddressByName(SmartContractConstants.CrossChainContractSystemHashName);
        Assert(Context.Sender == address, "Unauthorized to set genesis contract state.");

        CreateParliamentOrganizationForInitialControllerAddress(input.Value);
        return new Empty();
    }

    public override Empty SetContractProposalExpirationTimePeriod(SetContractProposalExpirationTimePeriodInput input)
    {
        AssertSenderAddressWith(State.ContractDeploymentController.Value.OwnerAddress);
        State.ContractProposalExpirationTimePeriod.Value = input.ExpirationTimePeriod;
        return new Empty();
    }
    
    public override Hash DeployUserSmartContract(ContractDeploymentInput input)
    {
        AssertUserDeployContract();
        
        var codeHash = HashHelper.ComputeFrom(input.Code.ToByteArray());
        Context.LogDebug(() => "BasicContractZero - Deployment user contract hash: " + codeHash.ToHex());
        
        Assert(State.SmartContractRegistrations[codeHash] == null, "Contract code has already been deployed before.");
        
        var proposedContractInputHash = CalculateHashFromInput(input);
        SendUserContractProposal(proposedContractInputHash,
            nameof(BasicContractZeroImplContainer.BasicContractZeroImplBase.ReleaseDeployUserSmartContract),
            input.ToByteString());

        // Fire event to trigger BPs checking contract code
        Context.Fire(new CodeCheckRequired
        {
            Code = input.Code,
            ProposedContractInputHash = proposedContractInputHash,
            Category = input.Category,
            IsSystemContract = false,
            IsUserContract = true
        });

        return codeHash;
    }

    public override Empty UpdateUserSmartContract(ContractUpdateInput input)
    {
        var info = State.ContractInfos[input.Address];
        Assert(info != null, "Contract not found.");
        Assert(Context.Sender == info.Author, "No permission.");
        var codeHash = HashHelper.ComputeFrom(input.Code.ToByteArray());
        Assert(info.CodeHash != codeHash, "Code is not changed.");
        Assert(State.SmartContractRegistrations[codeHash] == null, "Contract code has already been deployed before.");
        
        var proposedContractInputHash = CalculateHashFromInput(input);
        SendUserContractProposal(proposedContractInputHash,
            nameof(BasicContractZeroImplContainer.BasicContractZeroImplBase.ReleaseUpdateUserSmartContract),
            input.ToByteString());
        
        // Fire event to trigger BPs checking contract code
        Context.Fire(new CodeCheckRequired
        {
            Code = input.Code,
            ProposedContractInputHash = proposedContractInputHash,
            Category = info.Category,
            IsSystemContract = false,
            IsUserContract = true
        });
        
        return new Empty();
    }
    
    public override Empty ReleaseApprovedUserSmartContract(ReleaseContractInput input)
    {
        var contractProposingInput = State.ContractProposingInputMap[input.ProposedContractInputHash];

        Assert(
            contractProposingInput != null &&
            contractProposingInput.Status == ContractProposingInputStatus.CodeCheckProposed &&
            contractProposingInput.Proposer == Context.Self, "Invalid contract proposing status.");
        
        AssertCurrentMiner();
        
        contractProposingInput.Status = ContractProposingInputStatus.CodeChecked;
        State.ContractProposingInputMap[input.ProposedContractInputHash] = contractProposingInput;
        var codeCheckController = State.CodeCheckController.Value;
        Context.SendInline(codeCheckController.ContractAddress,
            nameof(AuthorizationContractContainer.AuthorizationContractReferenceState.Release), input.ProposalId);
        return new Empty();
    }

    public override Address ReleaseDeployUserSmartContract(ContractDeploymentInput input)
    {
        RequireSenderAuthority(State.CodeCheckController.Value.OwnerAddress);

        var inputHash = CalculateHashFromInput(input);
        TryClearContractProposingData(inputHash, out var contractProposingInput);

        var address = DeploySmartContract(null, input.Category, input.Code.ToByteArray(), false,
            contractProposingInput.Author);
        return address;
    }

    public override Empty ReleaseUpdateUserSmartContract(ContractUpdateInput input)
    {
        RequireSenderAuthority(State.CodeCheckController.Value.OwnerAddress);
        
        var inputHash = CalculateHashFromInput(input);
        TryClearContractProposingData(inputHash, out var proposingInput);

        UpdateSmartContract(input.Address, input.Code.ToByteArray(), proposingInput.Author);
        
        return new Empty();
    }

    public override Empty SetContractAuthor(SetContractAuthorInput input)
    {
        var info = State.ContractInfos[input.ContractAddress];
        Assert(info != null, "Contract not found.");
        var oldAuthor = info.Author;
        Assert(Context.Sender == info.Author, "No permission.");
        info.Author = input.NewAuthor;
        State.ContractInfos[input.ContractAddress] = info;
        Context.Fire(new AuthorUpdated()
        {
            Address = input.ContractAddress,
            OldAuthor = oldAuthor,
            NewAuthor = input.NewAuthor
        });
        
        return new Empty();
    }

    #endregion Actions
}
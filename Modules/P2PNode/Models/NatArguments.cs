using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessorApplication.Models;

record NatIntroductionRequest(
    string InitiatorHashKey,
    string TargetHashKey,
    string InitiatorPublicIP,
    int InitiatorPublicPort);

record NatIntroductionResponse(
    string ResponderHashKey,
    string ResponderPublicIP,
    int ResponderPublicPort);

using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.ElasticLoadBalancingV2.Targets;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace AwsEc2;

public class AwsEc2Stack : Stack
{
    internal AwsEc2Stack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
    {
        // Lookup the default VPC
        var vpc = Vpc.FromLookup(this, "DefaultVpc", new VpcLookupOptions
        {
            IsDefault = true
        });

        // Create a security group
        var securityGroup = new SecurityGroup(this, "WebApiSecurityGroup", new SecurityGroupProps
        {
            Vpc = vpc,
            AllowAllOutbound = true,
            Description = "Allow SSH and HTTP",
            SecurityGroupName = "WebApiSecurityGroup"
        });

        securityGroup.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(22), "Allow SSH access");
        securityGroup.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(80), "Allow HTTP access");
        securityGroup.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(5000), "MyApi App access");

        // Role for EC2
        var role = new Role(this, "Ec2Role", new RoleProps
        {
            AssumedBy = new ServicePrincipal("ec2.amazonaws.com")
        });

        role.AddManagedPolicy(ManagedPolicy.FromAwsManagedPolicyName("AmazonSSMManagedInstanceCore"));

        // Create EC2 Instance
        var machineImage = MachineImage.LatestAmazonLinux2023();
        var keyPair = KeyPair.FromKeyPairName(this, "key-0a498c763ef8ab954", "my-key-pair");
        var instance1 = new Instance_(this, "WebApiInstance", new InstanceProps
        {
            InstanceType = InstanceType.Of(InstanceClass.BURSTABLE3, InstanceSize.MICRO),
            MachineImage = machineImage,
            Vpc = vpc,
            Role = role,
            SecurityGroup = securityGroup,
            KeyPair = keyPair,
        });
        
        var instance2 = new Instance_(this, "WebApiInstance2", new InstanceProps
        {
            InstanceType = InstanceType.Of(InstanceClass.BURSTABLE3, InstanceSize.MICRO),
            MachineImage = machineImage,
            Vpc = vpc,
            Role = role,
            SecurityGroup = securityGroup,
            KeyPair = keyPair,
        });
        
        var lb = new ApplicationLoadBalancer(this, "MyALB", new ApplicationLoadBalancerProps {
            Vpc = vpc,
            InternetFacing = true,
            SecurityGroup = securityGroup
        });

        var listener = lb.AddListener("Listener", new BaseApplicationListenerProps {
            Port = 80,
            Open = true
        });
        
        var targetGroup = new ApplicationTargetGroup(this, "WebApiTargetGroup", new ApplicationTargetGroupProps
        {
            Vpc = vpc,
            Port = 5000,
            Protocol = ApplicationProtocol.HTTP,
            TargetType = TargetType.INSTANCE,
            HealthCheck = new HealthCheck
            {
                Path = "/weatherforecast",
                Port = "5000"
            }
        });

// Add EC2s to target group
        targetGroup.AddTarget(new InstanceTarget(instance1));
        targetGroup.AddTarget(new InstanceTarget(instance2));

// Attach target group to listener
        listener.AddTargetGroups("AddWebApiTargetGroup", new AddApplicationTargetGroupsProps
        {
            TargetGroups = new[] { targetGroup }
        });

        // S3 bucket for CodeDeploy artifacts
        var deployBucket = new Bucket(this, "CodeDeployArtifactsBucket", new BucketProps {
            Versioned = true,
            RemovalPolicy = RemovalPolicy.DESTROY,
            AutoDeleteObjects = true
        });

        new CfnOutput(this, "LoadBalancerDNS", new CfnOutputProps
        {
            Value = lb.LoadBalancerDnsName,
            Description = "DNS of the load balancer"
        });

        new CfnOutput(this, "CodeDeployArtifactsBucketName", new CfnOutputProps {
            Value = deployBucket.BucketName,
            Description = "S3 bucket for CodeDeploy artifacts"
        });
    }
}